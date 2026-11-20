#!/usr/bin/env python3
"""Host-owned M1 run ledger: crash-safe SQLite checkpoints and fail-closed cleanup.

Only fixed metadata is stored (IDs, states, hash, container name, result
category, evidence filename). Never store prompts, model output, stderr,
credentials, source, arbitrary child-process data or arbitrary file paths.
"""
from __future__ import annotations

import datetime as dt
import os
import pathlib
import re
import secrets
import sqlite3
import stat
import subprocess

try:
    import fcntl  # Linux / WSL2
except ImportError:
    fcntl = None

RUN_RE = re.compile(r"[0-9a-f]{24}\Z")
REQUEST_RE = re.compile(r"[0-9a-f]{32}\Z")
HASH_RE = re.compile(r"[0-9a-f]{64}\Z")
CONTAINER_RE = re.compile(r"ca-m17-acpx-[0-9a-f]{12}\Z")
EVIDENCE_RE = re.compile(r"m112-approved-coding-[0-9a-f]{12}\.json\Z")
LABEL = "io.github.dvdhtzr.codingassistant.runid"
TERMINAL = frozenset(("SUCCEEDED", "FAILED", "DECLINED", "INTERRUPTED_CLEAN"))
ACTIVE = frozenset(("NEW", "PREFLIGHT", "AWAITING_APPROVAL", "APPROVED",
                    "RUNNING", "VERIFYING"))
STATES = TERMINAL | ACTIVE | {"RECOVERY_BLOCKED"}
TRANSITIONS = {
    "NEW": {"PREFLIGHT", "RECOVERY_BLOCKED"},
    "PREFLIGHT": {"AWAITING_APPROVAL", "FAILED", "RECOVERY_BLOCKED"},
    "AWAITING_APPROVAL": {"APPROVED", "DECLINED", "FAILED", "RECOVERY_BLOCKED"},
    "APPROVED": {"RUNNING", "FAILED", "RECOVERY_BLOCKED"},
    "RUNNING": {"VERIFYING", "FAILED", "RECOVERY_BLOCKED"},
    "VERIFYING": {"SUCCEEDED", "FAILED", "RECOVERY_BLOCKED"},
    "RECOVERY_BLOCKED": {"INTERRUPTED_CLEAN"},
}
RESULT_CODES = frozenset((
    "GATEWAY_APPROVED_CODING_PASS", "BLOCKED", "NOT_APPROVED",
    "RECOVERED_ORPHAN", "CLEANUP_UNVERIFIED",
))


def utc() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")


def _validated_file(path: pathlib.Path) -> None:
    if path.is_symlink():
        raise ValueError("Refuse linked ledger path")
    if path.exists():
        info = path.stat()
        if not stat.S_ISREG(info.st_mode):
            raise ValueError("Ledger file is not regular")
        if os.name == "posix" and (info.st_uid != os.getuid() or
                                   info.st_mode & 0o077):
            raise ValueError("Ledger file must be owner-private")


def _directory(path: pathlib.Path) -> None:
    if path.is_symlink():
        raise ValueError("Refuse linked ledger directory")
    path.mkdir(mode=0o700, parents=True, exist_ok=True)
    if os.name == "posix":
        info = path.stat()
        if (info.st_uid != os.getuid() or info.st_mode & 0o077):
            raise ValueError("Ledger directory must be owner-private")


def _flock(fd: int, blocking: bool) -> bool:
    if fcntl is not None:
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | (0 if blocking else fcntl.LOCK_NB))
            return True
        except BlockingIOError:
            return False
    if os.name == "nt":
        import msvcrt
        os.lseek(fd, 0, os.SEEK_SET)
        try:
            msvcrt.locking(fd, msvcrt.LK_LOCK if blocking else msvcrt.LK_NBLCK, 1)
            return True
        except OSError:
            return False
    raise RuntimeError("No OS-backed exclusive file locks available")


def _unlock(fd: int) -> None:
    if fcntl is not None:
        fcntl.flock(fd, fcntl.LOCK_UN)
    elif os.name == "nt":
        import msvcrt
        os.lseek(fd, 0, os.SEEK_SET)
        msvcrt.locking(fd, msvcrt.LK_UNLCK, 1)


def _open_lock(path: pathlib.Path, create: bool) -> int:
    _validated_file(path)
    flags = os.O_RDWR | getattr(os, "O_NOFOLLOW", 0)
    if create:
        flags |= os.O_CREAT | os.O_EXCL
    fd = os.open(path, flags, 0o600)
    if create:
        os.write(fd, b"\0")  # Windows byte-range lock needs existing byte.
    else:
        _validated_file(path)
    return fd


def _result_code(value: str | None) -> str | None:
    if value is not None and value not in RESULT_CODES:
        raise ValueError("Unrecognized result category")
    return value


class DuplicateRequest(ValueError):
    def __init__(self, record: dict):
        super().__init__("Idempotency key already used; existing run returned")
        self.record = record


class RunLedger:
    def __init__(self, directory: pathlib.Path):
        self.directory = directory
        _directory(directory)
        self.lock_dir = directory / "run-locks"
        _directory(self.lock_dir)
        db_path = directory / "runs.sqlite3"
        _validated_file(db_path)
        fd = os.open(db_path, os.O_WRONLY | os.O_CREAT | getattr(os, "O_NOFOLLOW", 0),
                     0o600)
        os.close(fd)
        _validated_file(db_path)
        self.db = sqlite3.connect(db_path, timeout=10)
        self.db.row_factory = sqlite3.Row
        self.db.execute("PRAGMA foreign_keys=ON")
        self.db.execute("PRAGMA busy_timeout=10000")
        self.db.execute("PRAGMA synchronous=FULL")
        self.db.execute("PRAGMA journal_mode=DELETE")
        with self.db:
            self.db.execute("""
              CREATE TABLE IF NOT EXISTS runs (
                run_id TEXT PRIMARY KEY,
                request_id TEXT UNIQUE,
                state TEXT NOT NULL,
                plan_hash TEXT,
                container_name TEXT UNIQUE,
                result_code TEXT,
                evidence_name TEXT,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
              )
            """)
            self.db.execute("""
              CREATE TABLE IF NOT EXISTS transitions (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL REFERENCES runs(run_id),
                state TEXT NOT NULL,
                changed_utc TEXT NOT NULL
              )
            """)

    def close(self) -> None:
        self.db.close()

    def __enter__(self) -> "RunLedger":
        return self

    def __exit__(self, *_unused) -> None:
        self.close()

    def get(self, run_id: str) -> dict:
        if not RUN_RE.fullmatch(run_id):
            raise ValueError("Invalid run identifier")
        row = self.db.execute(
            "SELECT * FROM runs WHERE run_id = ?", (run_id,)).fetchone()
        if row is None:
            raise ValueError("Run not found")
        return dict(row)

    def history(self, limit: int = 20) -> list[dict]:
        if type(limit) is not int or not 1 <= limit <= 100:
            raise ValueError("Invalid history limit")
        return [dict(r) for r in self.db.execute(
            "SELECT * FROM runs ORDER BY created_utc DESC, run_id DESC LIMIT ?",
            (limit,)).fetchall()]

    def transitions(self, run_id: str) -> list[str]:
        self.get(run_id)
        return [r["state"] for r in self.db.execute(
            "SELECT state FROM transitions WHERE run_id = ? ORDER BY seq",
            (run_id,)).fetchall()]

    def start(self, request_id: str | None = None) -> "RunHandle":
        if request_id is not None and not REQUEST_RE.fullmatch(request_id):
            raise ValueError("Idempotency key must be exactly 32 lowercase hex chars")
        if request_id is not None:
            row = self.db.execute(
                "SELECT run_id FROM runs WHERE request_id = ?", (request_id,)).fetchone()
            if row is not None:
                raise DuplicateRequest(self.get(row["run_id"]))
        for _ in range(3):
            run_id = secrets.token_hex(12)
            lock_path = self.lock_dir / (run_id + ".lock")
            try:
                fd = _open_lock(lock_path, create=True)
            except FileExistsError:
                continue
            try:
                if not _flock(fd, blocking=False):
                    raise RuntimeError("Could not hold new run lock")
                now = utc()
                with self.db:
                    self.db.execute(
                        "INSERT INTO runs (run_id,request_id,state,created_utc,updated_utc)"
                        " VALUES (?,?, 'NEW',?,?)", (run_id, request_id, now, now))
                    self.db.execute(
                        "INSERT INTO transitions (run_id,state,changed_utc)"
                        " VALUES (?,'NEW',?)", (run_id, now))
                return RunHandle(self, run_id, fd)
            except sqlite3.IntegrityError:
                _unlock(fd)
                os.close(fd)
                if request_id:
                    row = self.db.execute(
                        "SELECT run_id FROM runs WHERE request_id = ?", (request_id,)
                    ).fetchone()
                    if row:
                        raise DuplicateRequest(self.get(row["run_id"]))
                raise
            except BaseException:
                _unlock(fd)
                os.close(fd)
                raise
        raise RuntimeError("Could not allocate unique run identity")

    def _advance(self, run_id: str, to: str, *,
                 plan_hash: str | None = None,
                 container_name: str | None = None,
                 result_code: str | None = None,
                 evidence_name: str | None = None) -> None:
        if to not in STATES:
            raise ValueError("Unrecognized state")
        if plan_hash is not None and not HASH_RE.fullmatch(plan_hash):
            raise ValueError("Invalid approved plan hash")
        if container_name is not None and not CONTAINER_RE.fullmatch(container_name):
            raise ValueError("Invalid managed container name")
        if evidence_name is not None and not EVIDENCE_RE.fullmatch(evidence_name):
            raise ValueError("Invalid evidence file name")
        _result_code(result_code)
        with self.db:
            row = self.db.execute(
                "SELECT state,plan_hash,container_name FROM runs WHERE run_id=?",
                (run_id,)).fetchone()
            if row is None or to not in TRANSITIONS.get(row["state"], ()):
                raise ValueError("Invalid durable run transition")
            if plan_hash is not None and (row["plan_hash"] is not None or
                                          to != "AWAITING_APPROVAL"):
                raise ValueError("Plan is immutable after preview")
            if container_name is not None and (row["container_name"] is not None or
                                                to != "RUNNING"):
                raise ValueError("Container must be registered before launch")
            if to == "APPROVED" and not row["plan_hash"]:
                raise ValueError("Missing immutable exact approval plan")
            if to == "RUNNING" and not container_name:
                raise ValueError("Cannot run unregistered container")
            if to in TERMINAL and result_code is None:
                raise ValueError("Missing terminal result")
            now = utc()
            updated = self.db.execute("""
                UPDATE runs SET state=?, updated_utc=?,
                  plan_hash=COALESCE(?,plan_hash),
                  container_name=COALESCE(?,container_name),
                  result_code=COALESCE(?,result_code),
                  evidence_name=COALESCE(?,evidence_name)
                WHERE run_id=? AND state=?
            """, (to, now, plan_hash, container_name, result_code,
                  evidence_name, run_id, row["state"]))
            if updated.rowcount != 1:
                raise ValueError("Run state concurrently modified")
            self.db.execute(
                "INSERT INTO transitions (run_id,state,changed_utc) VALUES (?,?,?)",
                (run_id, to, now))

    def reconcile(self, run_id: str, *, runner=None) -> dict:
        """No model invocation; never trust an unverified existing container.

        A live lock means the original owner may still be executing. A dead
        lock plus matching Podman label permits cleanup, but NEVER a replay
        or invented successful verification.
        """
        record = self.get(run_id)
        if record["state"] in TERMINAL:
            return {"result": "ALREADY_TERMINAL", "run": record}
        lock_path = self.lock_dir / (run_id + ".lock")
        if not lock_path.exists() or lock_path.is_symlink():
            return {"result": "RECOVERY_BLOCKED", "run": record}
        fd = _open_lock(lock_path, create=False)
        try:
            if not _flock(fd, blocking=False):
                return {"result": "ACTIVE", "run": record}
            # Recheck under the exclusive run lock and a current DB read.
            record = self.get(run_id)
            if record["state"] in TERMINAL:
                return {"result": "ALREADY_TERMINAL", "run": record}
            name = record["container_name"]
            clean = True
            if name is not None:
                if not CONTAINER_RE.fullmatch(name) or name != managed_container(run_id):
                    clean = False
                else:
                    command = runner or _fixed
                    try:
                        exists = command(
                            ["podman", "container", "exists", name], timeout=20)
                        if exists.returncode == 0:
                            inspect = command([
                                "podman", "inspect", "--format",
                                '{{index .Config.Labels "' + LABEL + '"}}', name,
                            ], timeout=20)
                            if (inspect.returncode != 0 or
                                    inspect.stdout.strip() != run_id):
                                clean = False
                            else:
                                removed = command(
                                    ["podman", "rm", "-f", name], timeout=30)
                                check = command(
                                    ["podman", "container", "exists", name], timeout=20)
                                clean = removed.returncode == 0 and check.returncode == 1
                        elif exists.returncode != 1:
                            clean = False
                    except (OSError, subprocess.TimeoutExpired):
                        clean = False
            if not clean:
                if record["state"] != "RECOVERY_BLOCKED":
                    self._advance(run_id, "RECOVERY_BLOCKED")
                return {"result": "RECOVERY_BLOCKED", "run": self.get(run_id)}
            # Failed/uncertain run, NEVER resume or infer success from artifacts.
            if record["state"] != "RECOVERY_BLOCKED":
                self._advance(run_id, "RECOVERY_BLOCKED")
            self._advance(run_id, "INTERRUPTED_CLEAN",
                          result_code="RECOVERED_ORPHAN")
            return {"result": "INTERRUPTED_CLEAN", "run": self.get(run_id)}
        finally:
            try:
                _unlock(fd)
            finally:
                os.close(fd)


class RunHandle:
    def __init__(self, ledger: RunLedger, run_id: str, fd: int):
        self.ledger = ledger
        self.run_id = run_id
        self.fd = fd

    def advance(self, state: str, **kwargs) -> None:
        self.ledger._advance(self.run_id, state, **kwargs)

    def container_name(self) -> str:
        return managed_container(self.run_id)

    def finish(self, report: dict, evidence_name: str) -> str:
        if type(report) is not dict:
            raise ValueError("Missing execution result")
        prior = self.ledger.get(self.run_id)["state"]
        if prior in TERMINAL:
            return prior
        if report.get("status") == "GATEWAY_APPROVED_CODING_PASS":
            if prior != "VERIFYING":
                raise ValueError("Cannot mark success without verified execution phase")
            target, code = "SUCCEEDED", "GATEWAY_APPROVED_CODING_PASS"
        elif report.get("status") == "NOT_APPROVED":
            if prior != "DECLINED":
                raise ValueError("Decline not journaled before completion")
            return prior
        else:
            gateway = report.get("gateway")
            if type(gateway) is dict and gateway.get("removed") is False and (
                    prior in ("RUNNING", "VERIFYING")):
                target, code = "RECOVERY_BLOCKED", None
            else:
                target, code = "FAILED", "BLOCKED"
            if target == "FAILED" and prior == "NEW":
                self.advance("PREFLIGHT")
        self.advance(target, result_code=code, evidence_name=evidence_name)
        return target

    def close(self) -> None:
        if self.fd is not None:
            fd, self.fd = self.fd, None
            try:
                _unlock(fd)
            finally:
                os.close(fd)


def managed_container(run_id: str) -> str:
    if not RUN_RE.fullmatch(run_id):
        raise ValueError("Invalid managed run id")
    return "ca-m17-acpx-" + run_id[:12]


def _fixed(command: list[str], timeout: int) -> subprocess.CompletedProcess:
    return subprocess.run(command, stdin=subprocess.DEVNULL,
                          capture_output=True, text=True, timeout=timeout,
                          check=False)
