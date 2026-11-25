#!/usr/bin/env python3
"""Owner-gated pilot: REAL OpenClaw/ACPX/OpenCode on an approved arbitrary .NET task.

This is NOT yet the Mission Control build API. It is a deliberately bounded
qualification driver: one verified local model turn, an empty disposable
source workspace, and an independent offline build/test guest. Nothing from
the owner's existing repositories is mounted. No inference/cloud fallback.
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import hmac
import json
import os
import pathlib
import re
import secrets
import selectors
import shutil
import sqlite3
import stat
import urllib.parse
import subprocess
import sys
import tempfile
import time
import uuid
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "tools/m1"), str(ROOT / "tools/m0")]
from approved_gateway_coding import (
    ARTIFACTS, MODEL, HEX64, broker_for, fixed, preflight, run_gateway,
)
from ollama_bridge import serve_bridge
from run_ledger import RunLedger, DuplicateRequest

SCHEMA = "codingassistant-task-contract-v1"
PRIVATE_LEDGER = ARTIFACTS / "private-task-ledger"
MAX_CONTRACT_BYTES = 24576
MAX_FILE_BYTES = 16000
PATH_SEGMENT = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,70}\Z")
EXTENSIONS = frozenset((
    ".cs", ".csproj", ".ts", ".tsx", ".js", ".jsx", ".css",
    ".html", ".json", ".md", ".txt", ".py", ".sln", ".yml", ".yaml",
))
EXPECTED_KEYS = frozenset((
    "schema", "taskId", "specificationVersion",
    "approvedSpecificationSha256", "specificationSha256",
    "objective", "specification", "model", "files",
))
RESULT_LIMIT = 80_000


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def safe_path(name: object) -> str:
    if (type(name) is not str or len(name) < 3 or len(name) > 140 or
            ":" in name or "\\" in name or "//" in name or
            name.startswith("/")):
        raise ValueError("Invalid task-specific source path")
    parts = name.split("/")
    if (len(parts) > 4 or any(
        not PATH_SEGMENT.fullmatch(part) or part.startswith(".") or
        part.endswith(".") or part.lower() in
        {"node_modules", "bin", "obj", "artifacts"}
        for part in parts
    ) or pathlib.PurePosixPath(name).suffix.lower() not in EXTENSIONS):
        raise ValueError("Invalid task-specific source path")
    return name


def no_duplicate_keys(pairs):
    output = {}
    for key, value in pairs:
        if key in output:
            raise ValueError("Duplicate contract key")
        output[key] = value
    return output


def read_contract(path: pathlib.Path) -> tuple[dict, bytes, str]:
    if path.is_symlink() or not path.is_file():
        raise ValueError("Linked or absent task contract")
    metadata = path.stat()
    if (not stat.S_ISREG(metadata.st_mode) or metadata.st_nlink != 1 or
            (os.name == "posix" and
             (metadata.st_uid != os.getuid() or metadata.st_mode & 0o077))):
        raise ValueError("Task contract must be owner-private")
    if not 0 < metadata.st_size <= MAX_CONTRACT_BYTES:
        raise ValueError("Invalid contract byte size")
    data = path.read_bytes()
    if len(data) != metadata.st_size or b"\x00" in data:
        raise ValueError("Task contract changed during read")
    value = json.loads(data.decode("utf-8"), object_pairs_hook=no_duplicate_keys)
    if type(value) is not dict or set(value) != EXPECTED_KEYS:
        raise ValueError("Task contract schema mismatch")
    if (value["schema"] != SCHEMA or value["model"] != MODEL or
            type(value["taskId"]) is not str or
            not re.fullmatch(r"[0-9a-f]{32}", value["taskId"]) or
            type(value["specificationVersion"]) is not int or
            not 1 <= value["specificationVersion"] <= 1_000_000 or
            type(value["objective"]) is not str or
            not 8 <= len(value["objective"]) <= 4000 or
            not value["objective"].strip() or
            type(value["specification"]) is not str or
            not 32 <= len(value["specification"]) <= 16000 or
            not value["specification"].strip()):
        raise ValueError("Invalid task or approved model")
    for key in ("approvedSpecificationSha256", "specificationSha256"):
        if type(value[key]) is not str or HEX64.fullmatch(value[key]) is None:
            raise ValueError("Invalid specification SHA256")
    text = value["specification"]
    if sha(text.encode("utf-8")) != value["specificationSha256"]:
        raise ValueError("Specification text changed after hashing")
    # The authoritative interview store hashes the task, version and text.
    approval = (f"codingassistant-spec-v1\n{value['taskId']}\n"
                f"{value['specificationVersion']}\n{text}").encode("utf-8")
    if sha(approval) != value["approvedSpecificationSha256"]:
        raise ValueError("Not an exact approved interview specification")
    names = value["files"]
    if type(names) is not list or not 1 <= len(names) <= 5:
        raise ValueError("Invalid file allowlist")
    names = [safe_path(name) for name in names]
    if len({name.casefold() for name in names}) != len(names):
        raise ValueError("Duplicate output paths")
    # Initial pilot supports ordinary offline .NET app projects only;
    # task objective/specification are NOT fixed or substituted.
    projects = [name for name in names if name.endswith(".csproj")]
    if not projects or any(not name.endswith((".cs", ".csproj")) for name in names):
        raise ValueError("Pilot requires an offline .NET source-file plan")
    return value, data, sha(data)


def default_database() -> pathlib.Path:
    selected = os.environ.get("CODINGASSISTANT_DATA_DIR")
    if selected:
        return pathlib.Path(selected) / "codingassistant.db"
    local = os.environ.get("XDG_DATA_HOME")
    return pathlib.Path(local or pathlib.Path.home() / ".local" / "share") / (
        "CodingAssistant/codingassistant.db")


def verify_live_approval(path: pathlib.Path, contract: dict) -> None:
    """No revoked/stale interview or obsolete file plan can admit the agent.

    SQLite is the authoritative current state; a matching content/hash alone
    is NOT sufficient after a later specification revision.
    """
    if path.is_symlink() or not path.is_file():
        raise ValueError("Private local task database unavailable")
    info = path.stat()
    if (not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or
            info.st_uid != os.getuid() or info.st_mode & 0o077):
        raise ValueError("Task database must be owned and private")
    uri = "file:" + urllib.parse.quote(str(path.resolve())) + "?mode=ro"
    try:
        with sqlite3.connect(uri, uri=True, timeout=5) as db:
            task = db.execute(
                "SELECT objective FROM tasks WHERE id=?",
                (contract["taskId"],)).fetchone()
            spec = db.execute("""
                SELECT version,sha256,model,content,status
                  FROM interview_specs WHERE task_id=?
                  ORDER BY version DESC LIMIT 1
            """, (contract["taskId"],)).fetchone()
            job = db.execute("""
                SELECT id,spec_version,spec_sha256,model,state
                  FROM project_drafts WHERE task_id=?
                  ORDER BY created_utc DESC,id DESC LIMIT 1
            """, (contract["taskId"],)).fetchone()
            if (task is None or task[0] != contract["objective"] or
                spec is None or spec != (
                    contract["specificationVersion"],
                    contract["approvedSpecificationSha256"],
                    contract["model"], contract["specification"], "Approved") or
                job is None or job[1:] != (
                    contract["specificationVersion"],
                    contract["approvedSpecificationSha256"],
                    contract["model"], "Generated")):
                raise ValueError("Task approval or planned file scope has changed")
            paths = db.execute(
                "SELECT path FROM project_draft_files WHERE draft_id=?",
                (job[0],)).fetchall()
            if sorted(path[0] for path in paths) != sorted(contract["files"]):
                raise ValueError("Planned project paths have changed")
    except sqlite3.Error:
        raise ValueError("Current owner approval could not be verified") from None


def execution_plan(contract: dict, digest: str, image: str,
                   model_digest: str, evaluator: str) -> tuple[dict, str]:
    if any(HEX64.fullmatch(x) is None for x in (digest, image, model_digest, evaluator)):
        raise ValueError("Unsigned task runtime")
    plan = {
        "schema": "codingassistant-task-agent-pilot-v1",
        "task_id": contract["taskId"],
        "spec_version": contract["specificationVersion"],
        "approved_spec_sha256": contract["approvedSpecificationSha256"],
        "contract_sha256": digest,
        "model": MODEL,
        "model_digest": model_digest,
        "image_id": image,
        "evaluator_image_id": evaluator,
        "agent_source_sha256": sha(pathlib.Path(__file__).read_bytes()),
        "writable_paths": sorted(contract["files"]),
        "scope": "disposable-project-only",
        "network": "none-except-local-pinned-ollama-socket",
        "git_remote": False,
        "max_agent_turns": 1,
    }
    digest = sha(json.dumps(plan, sort_keys=True, separators=(",", ":")).encode())
    return plan, digest


def source_snapshot(workspace: pathlib.Path, allowed: set[str],
                    *, permit_build_outputs: bool = False) -> dict[str, str]:
    if workspace.is_symlink() or not workspace.is_dir():
        raise ValueError("Unsafe workspace root")
    found: dict[str, str] = {}
    total = 0
    count = 0
    for entry in workspace.rglob("*"):
        count += 1
        if count > 2000 or entry.is_symlink():
            raise ValueError("Unexpected workspace entry")
        relative = entry.relative_to(workspace).as_posix()
        parts = entry.relative_to(workspace).parts
        is_build = permit_build_outputs and any(
            part.lower() in ("bin", "obj") for part in parts)
        if entry.is_dir():
            if is_build or any(name.startswith(relative + "/") for name in allowed):
                continue
            raise ValueError("Unknown workspace directory")
        if not entry.is_file() or entry.stat().st_nlink != 1:
            raise ValueError("Unsupported source or hardlink")
        if relative not in allowed and not is_build:
            raise ValueError("Agent wrote outside its approved file allowlist")
        size = entry.stat().st_size
        total += size
        if total > 80 * 1024 * 1024 or size > (20 * 1024 * 1024 if is_build
                                               else MAX_FILE_BYTES):
            raise ValueError("Oversized source or build output")
        if relative in allowed:
            if size == 0:
                raise ValueError("Empty generated source file")
            found[relative] = sha(entry.read_bytes())
    if set(found) != allowed:
        raise ValueError("Missing expected generated files")
    return found


def evaluator_command(image: str, worktree: pathlib.Path,
                      project: str, command: list[str], *,
                      name: str | None = None) -> list[str]:
    if HEX64.fullmatch(image) is None or safe_path(project) != project:
        raise ValueError("Invalid offline evaluator provenance")
    if name is not None and re.fullmatch(r"ca-task-eval-[0-9a-f]{12}", name) is None:
        raise ValueError("Unsafe evaluator container identity")
    # Every compiler/test subprocess runs inside its OWN fresh no-network,
    # no-model-socket, non-root, read-only-root container.
    return [
        "podman", "run", "--rm",
        *([] if name is None else ["--name", name]),
        "--pull=never", "--network=none",
        "--read-only", "--cap-drop=all", "--security-opt=no-new-privileges",
        "--pids-limit=96", "--memory=3g", "--userns=keep-id",
        "--user", f"{os.getuid()}:{os.getgid()}",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=768m,notmpcopyup",
        "--volume", f"{worktree.resolve()}:/workspace:rw,nosuid,nodev",
        "--workdir=/workspace", "--env", "HOME=/tmp",
        "--env", "DOTNET_CLI_HOME=/tmp",
        "--env", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1",
        "--env", "DOTNET_NOLOGO=1",
        "--entrypoint=dotnet", image, *command,
    ]


def run_check(image: str, worktree: pathlib.Path,
              project: str, command: list[str], timeout: int) -> dict:
    """Bound captured guest output and explicitly reconcile evaluator cleanup.

    A generated .csproj may run arbitrary MSBuild targets. Never trust it
    to limit stdout, exit promptly, or clean up an orphan Podman process.
    """
    name = "ca-task-eval-" + secrets.token_hex(6)
    args = evaluator_command(image, worktree, project, command, name=name)
    captured = bytearray()
    deadline = time.monotonic() + timeout
    failed = None
    rc = None
    try:
        with subprocess.Popen(args, stdin=subprocess.DEVNULL,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              bufsize=0) as process:
            try:
                with selectors.DefaultSelector() as ready:
                    ready.register(process.stdout, selectors.EVENT_READ)
                    while True:
                        remaining = deadline - time.monotonic()
                        if remaining <= 0:
                            raise subprocess.TimeoutExpired(args, timeout)
                        events = ready.select(min(remaining, 0.5))
                        if not events:
                            continue
                        part = os.read(process.stdout.fileno(), 8192)
                        if not part:
                            break
                        captured.extend(part)
                        if len(captured) > RESULT_LIMIT:
                            raise ValueError("Untrusted evaluator stdout exceeded limit")
                rc = process.wait(timeout=max(1, deadline - time.monotonic()))
            except (OSError, ValueError, subprocess.TimeoutExpired) as error:
                failed = error
                if process.poll() is None:
                    process.kill()
                process.wait(timeout=10)
    finally:
        # --rm normally removes the guest. A killed/interrupted Podman client
        # may leave it alive. Only remove this uniquely named evaluator, then
        # independently verify it no longer exists.
        fixed(["podman", "rm", "-f", name], timeout=30)
        exists = fixed(["podman", "container", "exists", name], timeout=15)
        if exists.returncode != 1:
            raise ValueError("Isolated evaluator cleanup unverified")
    if failed is not None:
        raise ValueError("Bounded evaluator did not finish cleanly") from None
    stdout = captured.decode("utf-8", errors="replace")
    codes = sorted(set(re.findall(r"\\b(?:CS|NU)[0-9]{4}\\b", stdout)))[:12]
    return {
        "passed": rc == 0, "exit_code": rc,
        "diagnostic_codes": codes,
        "output_too_large": False,
        "tests_observed": sum(int(x) for x in re.findall(
            r"\\bPassed:\\s*([0-9]+)\\b", stdout)) if
            command[0] == "test" else 0,
    }


def independent_evaluation(image: str, workspace: pathlib.Path,
                           allowed: set[str]) -> dict:
    projects = sorted(name for name in allowed if name.endswith(".csproj"))
    checks: list[dict] = []
    tests_seen = 0
    for project in projects:
        # The no-network guest cannot fetch arbitrary external dependencies.
        for command, seconds in (
            (["restore", "/workspace/" + project, "--ignore-failed-sources",
              "--source", "/tmp", "-p:NuGetAudit=false"], 240),
            (["build", "/workspace/" + project, "--no-restore"], 240),
        ):
            result = run_check(image, workspace, project, command, seconds)
            checks.append({"project": project, "operation": command[0], **result})
            if not result["passed"]:
                return {"build_passed": False, "tests_passed": False,
                        "test_count": tests_seen, "checks": checks}
        if project.lower().endswith(".tests.csproj"):
            test = run_check(image, workspace, project,
                             ["test", "/workspace/" + project,
                              "--no-restore", "--no-build"], 240)
            checks.append({"project": project, "operation": "test", **test})
            tests_seen += test["tests_observed"]
            if not test["passed"]:
                return {"build_passed": True, "tests_passed": False,
                        "test_count": tests_seen, "checks": checks}
    return {"build_passed": True, "tests_passed": tests_seen > 0,
            "test_count": tests_seen, "checks": checks}


def save_project(workspace: pathlib.Path, hashes: dict[str, str],
                 target: pathlib.Path) -> None:
    fd = os.open(target, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    with os.fdopen(fd, "wb") as stream:
        with zipfile.ZipFile(stream, "w", zipfile.ZIP_DEFLATED) as archive:
            for name in sorted(hashes):
                content = (workspace / name).read_bytes()
                if sha(content) != hashes[name]:
                    raise ValueError("Source changed before export")
                archive.writestr(name, content)
        stream.flush()
        os.fsync(stream.fileno())


def run(contract_path: pathlib.Path, request_id: str | None, *,
        database: pathlib.Path, prepare: bool = False) -> int:
    if sys.platform != "linux" or os.geteuid() == 0:
        raise ValueError("Pilot requires Linux rootless Podman")
    contract, raw, contract_sha = read_contract(contract_path)
    verify_live_approval(database, contract)
    _, _, image, model_digest, evaluator, _ = preflight(prepare=prepare)
    plan, plan_sha = execution_plan(
        contract, contract_sha, image, model_digest, evaluator)
    # Preflight checks were model-free. This is a new consent scope, NOT the
    # legacy frozen M1.12 execution hash and NOT the interview approval.
    with RunLedger(PRIVATE_LEDGER) as ledger:
        try:
            journal = ledger.start(request_id=request_id)
        except DuplicateRequest:
            print("PILOT DUPLICATE_REQUEST: no agent restarted")
            return 2
        report = {
            "schema": "codingassistant-task-project-pilot-v1",
            "run_id": journal.run_id, "task_id": contract["taskId"],
            "contract_sha256": contract_sha, "plan_hash": plan_sha,
            "status": "BLOCKED", "phase": "preflight",
            "source": {}, "gateway": None, "broker": None,
            "evaluation": None, "artifact": None,
        }
        try:
            journal.advance("PREFLIGHT")
            journal.advance("AWAITING_APPROVAL", plan_hash=plan_sha)
            print("Disposable scoped project coding pilot")
            print("  model:", MODEL, "output files:", len(contract["files"]))
            print("  exact executable plan SHA256:", plan_sha)
            print("  no host repository mount, no cloud, no Git remote")
            print("Type APPROVE followed by the full SHA256 to authorize ONE run:")
            answer = sys.stdin.readline(160).rstrip("\r\n")
            if not hmac.compare_digest(answer, "APPROVE " + plan_sha):
                journal.advance("DECLINED", result_code="NOT_APPROVED")
                print("PILOT NOT_APPROVED; no agent executed")
                return 2
            # Owner approval is unusable if the live specification changed
            # during preflight/confirmation.
            verify_live_approval(database, contract)
            journal.advance("APPROVED")
            with tempfile.TemporaryDirectory(prefix="ca-task-agent-") as temporary:
                root = pathlib.Path(temporary)
                root.chmod(0o700)
                workspace = root / "approved"
                workspace.mkdir(mode=0o700)
                for name in contract["files"]:
                    (workspace / name).parent.mkdir(parents=True, exist_ok=True)
                input_dir = root / "task-contract"
                input_dir.mkdir(mode=0o700)
                input_path = input_dir / "task.json"
                fd = os.open(input_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
                with os.fdopen(fd, "wb") as stream:
                    stream.write(raw)
                input_path.chmod(0o400)
                ipc = root / "ipc"
                ipc.mkdir(mode=0o700)
                sentinel = root / "unmounted-sentinel"
                sentinel.write_text("not-mounted", encoding="utf-8")
                broker_state = {"attempts": 0, "verified": 0,
                                "failed": 0, "denied": 0}
                gateway_evidence: dict = {}
                report["gateway"] = gateway_evidence
                report["broker"] = broker_state
                report["phase"] = "coding_agent"
                with serve_bridge(ipc / "ollama.sock",
                                  broker_for(MODEL, model_digest, broker_state)):
                    gateway = run_gateway(
                        image, workspace, ipc, plan_sha, gateway_evidence,
                        journal=journal, task_contract_dir=input_dir,
                        task_contract_sha256=contract_sha)
                if (not gateway.get("removed") or broker_state["verified"] < 1 or
                    broker_state["denied"] or broker_state["failed"] or
                    sentinel.read_text(encoding="utf-8") != "not-mounted"):
                    raise ValueError("Model or OS security boundary failed")
                allowed = set(contract["files"])
                before = source_snapshot(workspace, allowed)
                report["source"] = before
                report["phase"] = "offline_verification"
                journal.advance("VERIFYING")
                evaluation = independent_evaluation(evaluator, workspace, allowed)
                report["evaluation"] = evaluation
                after = source_snapshot(workspace, allowed, permit_build_outputs=True)
                if after != before:
                    raise ValueError("Independent evaluator modified source")
                artifact_name = "task-output-" + journal.run_id + ".zip"
                save_project(workspace, before, PRIVATE_LEDGER / artifact_name)
                report["artifact"] = artifact_name
                verify_live_approval(database, contract)
                if evaluation["build_passed"] and evaluation["tests_passed"]:
                    report["status"] = "TASK_PROJECT_VERIFIED"
                    report["phase"] = "complete"
                else:
                    report["phase"] = "build_or_test_not_verified"
        except (OSError, RuntimeError, ValueError, TypeError, KeyError,
                subprocess.TimeoutExpired) as error:
            report["reason_code"] = type(error).__name__
        finally:
            name = "m1-task-pilot-" + uuid.uuid4().hex[:12] + ".json"
            target = PRIVATE_LEDGER / name
            fd = os.open(target, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8") as stream:
                json.dump(report, stream, sort_keys=True, indent=2)
                stream.write("\n")
                stream.flush()
                os.fsync(stream.fileno())
            try:
                terminal = journal.finish(report, name)
            finally:
                journal.close()
        print("PILOT", terminal, report["phase"])
        if report["evaluation"]:
            outcome = report["evaluation"]
            print("  build:", outcome["build_passed"],
                  "tests:", outcome["tests_passed"],
                  "observed passing tests:", outcome["test_count"])
        print("  evidence:", target)
        if report["artifact"]:
            print("  source ZIP:", PRIVATE_LEDGER / report["artifact"])
        return 0 if terminal == "SUCCEEDED" else 2


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--contract", required=True, type=pathlib.Path,
                        help="Owner-private exact JSON bytes from API agent-contract-preview")
    parser.add_argument("--database", type=pathlib.Path, default=default_database(),
                        help="Current owner-private CodingAssistant SQLite database")
    parser.add_argument("--request-id",
                        help="Optional 32 lowercase hex idempotency key")
    parser.add_argument("--prepare", action="store_true",
                        help="Allow OFFLINE immutable image preparation before approval")
    args = parser.parse_args()
    if args.request_id is not None and not re.fullmatch(r"[0-9a-f]{32}", args.request_id):
        parser.error("Invalid idempotency key")
    try:
        return run(args.contract, args.request_id, database=args.database,
                   prepare=args.prepare)
    except (OSError, RuntimeError, ValueError, TypeError,
            subprocess.TimeoutExpired) as error:
        print("PILOT BLOCKED:", type(error).__name__)
        print("No autonomous project pass is claimed.")
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
