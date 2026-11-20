#!/usr/bin/env python3
"""M1.14: prove durable run recovery with a REAL rootless Podman orphan.

Linux-only, opt-in, entirely model-free. A disposable coordinator journals
a test-only run, starts a fixed idle Node child in a labeled, no-network
container, then exits via os._exit(73) while the guest is still running.
The parent proves the live lock protects the owner, then reconciles and
independently checks removal. No coding session, ACP request, Ollama call,
host source mount, credential, user worktree, or Git remote is permitted.
"""
from __future__ import annotations

import datetime as dt
import hashlib
import json
import os
import pathlib
import re
import select
import subprocess
import sys
import tempfile
import uuid

from acpx_initialize_probe import OWNER_IMAGE_ID
from run_ledger import (
    DuplicateRequest, LABEL, RunLedger, managed_container,
)
from sandbox_probe import inspect_podman

ROOT = pathlib.Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts/m1"
READY = re.compile(r"READY ([0-9a-f]{24})\n\Z")
# This is deliberately NOT the owner-approved coding plan. No model,
# prompt or writable project is ever started by this recovery exercise.
TEST_PLAN = hashlib.sha256(b"m1.14-model-free-crash-probe-only").hexdigest()
REQUEST_ID = hashlib.sha256(b"m1.14-model-free-crash-probe-request").hexdigest()[:32]
CHILD_EXIT = 73
IMAGE_RE = re.compile(r"[0-9a-f]{64}\Z")


class RecoveryProbeBlocked(ValueError):
    def __init__(self, code: str):
        if code not in {
            "PLATFORM", "ROOTLESS", "IMAGE_MISSING", "CHILD_START",
            "CHILD_NOT_READY", "CHILD_EXIT", "ACTIVE_LOCK_NOT_ENFORCED",
            "RUN_RECORD_MISMATCH", "GUEST_NOT_RUNNING", "LABEL_MISMATCH",
            "ORPHAN_STATE_LOST", "RECOVERY_NOT_CLEAN",
            "CONTAINER_REMAINS", "IDEMPOTENCY_FAILED",
            "RECOVERY_CLEANUP_BLOCKED",
        }:
            raise ValueError("Unrecognized bounded probe error code")
        self.code = code
        super().__init__(code)


def fixed(command: list[str], timeout: int) -> subprocess.CompletedProcess:
    return subprocess.run(
        command, stdin=subprocess.DEVNULL, capture_output=True,
        text=True, timeout=timeout, check=False)


def probe_command(image_id: str, run_id: str, uid: int, gid: int) -> list[str]:
    """A fixed idle guest with the M1.12 OS boundary and no bind mounts."""
    if (image_id != OWNER_IMAGE_ID or not IMAGE_RE.fullmatch(image_id) or
            uid <= 0 or gid <= 0 or
            not re.fullmatch(r"[0-9a-f]{24}\Z", run_id)):
        raise ValueError("Unapproved model-free recovery guest")
    name = managed_container(run_id)
    return [
        "podman", "run", "--detach", "--rm", "--pull=never",
        "--name", name, "--label", LABEL + "=" + run_id,
        "--network=none", "--read-only", "--cap-drop=all",
        "--security-opt=no-new-privileges", "--userns=keep-id",
        f"--user={uid}:{gid}",
        "--pids-limit=256", "--memory=3g", "--cpus=2",
        "--no-healthcheck",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=128m,notmpcopyup",
        "--env=HOME=/tmp", "--workdir=/tmp",
        "--entrypoint=node", image_id, "-e",
        "setInterval(() => {}, 1000)",
    ]


def _verify_guest(run_id: str) -> bool:
    """Report only a fixed boolean, never save raw Podman inspect output."""
    name = managed_container(run_id)
    label = fixed([
        "podman", "inspect", "--format",
        '{{index .Config.Labels "' + LABEL + '"}}', name,
    ], timeout=20)
    running = fixed(
        ["podman", "inspect", "--format", "{{.State.Running}}", name],
        timeout=20)
    return (label.returncode == 0 and label.stdout.strip() == run_id and
            running.returncode == 0 and running.stdout.strip() == "true")


def _child(directory: pathlib.Path, image_id: str) -> None:
    """Executed only as a separate disposable coordinator process."""
    # No user-approved coding is being impersonated here. A synthetic hash
    # exercises the state-machine shape in a SEPARATE private test database.
    try:
        with RunLedger(directory) as ledger:
            handle = ledger.start(request_id=REQUEST_ID)
            handle.advance("PREFLIGHT")
            handle.advance("AWAITING_APPROVAL", plan_hash=TEST_PLAN)
            handle.advance("APPROVED")
            handle.advance("RUNNING", container_name=handle.container_name())
            command = probe_command(
                image_id, handle.run_id, os.getuid(), os.getgid())
            started = fixed(command, timeout=55)
            if started.returncode != 0 or not _verify_guest(handle.run_id):
                os._exit(71)
            # The lock and SQLite connection must remain open while parent
            # checks live ownership. No model/ACP/session can be started.
            print("READY " + handle.run_id, flush=True)
            signal = sys.stdin.buffer.read(1)
            # Intentional abrupt exit: bypass Python finally/context cleanup
            # and leave the detached Podman guest + durable RUNNING record.
            os._exit(CHILD_EXIT if signal == b"X" else 72)
    except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired):
        os._exit(70)


def read_ready(process: subprocess.Popen, timeout: int = 100) -> str:
    if process.stdout is None:
        raise RecoveryProbeBlocked("CHILD_NOT_READY")
    readable, _, _ = select.select([process.stdout], [], [], timeout)
    if not readable:
        raise RecoveryProbeBlocked("CHILD_NOT_READY")
    line = process.stdout.readline()
    if not 0 < len(line) <= 36:
        raise RecoveryProbeBlocked("CHILD_NOT_READY")
    result = READY.fullmatch(line)
    if result is None:
        raise RecoveryProbeBlocked("CHILD_NOT_READY")
    return result.group(1)


def run_probe() -> dict:
    report = {
        "schema": 1, "stage": "M1.14-real-orphan-recovery",
        "recorded_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "BLOCKED", "reason": None,
        "run_id": None, "container_name": None, "image_id": OWNER_IMAGE_ID,
        "live_owner_protected": False, "label_verified": False,
        "crash_exit_verified": False, "durable_orphan_seen": False,
        "recovery_result": None, "container_removed": False,
        "duplicate_request_denied": False, "repeated_recovery_safe": False,
        "actual_owner_approval": False, "model_inference": False,
        "coding_session": False, "host_bind_mounts": False,
        "cloud_or_network_access": False,
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        report["reason"] = "PLATFORM"
        return report
    process = None
    directory = None
    try:
        if inspect_podman(20).get("rootless") is not True:
            raise RecoveryProbeBlocked("ROOTLESS")
        if fixed(["podman", "image", "exists", OWNER_IMAGE_ID], 25).returncode != 0:
            raise RecoveryProbeBlocked("IMAGE_MISSING")
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(
                dir=ARTIFACTS, prefix="ca-m114-recovery-") as temporary:
            directory = pathlib.Path(temporary) / "private-ledger"
            process = subprocess.Popen([
                sys.executable, str(pathlib.Path(__file__).resolve()),
                "--child", str(directory), OWNER_IMAGE_ID,
            ], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL, text=True, cwd=ROOT)
            try:
                run_id = read_ready(process)
                report["run_id"] = run_id
                report["container_name"] = managed_container(run_id)
                with RunLedger(directory) as ledger:
                    record = ledger.get(run_id)
                    if (record["state"] != "RUNNING" or
                            record["container_name"] != managed_container(run_id) or
                            record["plan_hash"] != TEST_PLAN or
                            record["request_id"] != REQUEST_ID):
                        raise RecoveryProbeBlocked("RUN_RECORD_MISMATCH")
                    if not _verify_guest(run_id):
                        raise RecoveryProbeBlocked("GUEST_NOT_RUNNING")
                    report["label_verified"] = True
                    # The living coordinator holds a separate exclusive lock.
                    if ledger.reconcile(run_id)["result"] != "ACTIVE":
                        raise RecoveryProbeBlocked("ACTIVE_LOCK_NOT_ENFORCED")
                    if not _verify_guest(run_id):
                        raise RecoveryProbeBlocked("GUEST_NOT_RUNNING")
                    report["live_owner_protected"] = True
                    # Crash ONLY the disposable coordinator. Never SIGKILL
                    # Podman, OpenCode, Ollama, Gateway, or another host PID.
                    process.stdin.write("X")
                    process.stdin.flush()
                    process.stdin.close()
                    if process.wait(timeout=12) != CHILD_EXIT:
                        raise RecoveryProbeBlocked("CHILD_EXIT")
                    report["crash_exit_verified"] = True
                    if ledger.get(run_id)["state"] != "RUNNING":
                        raise RecoveryProbeBlocked("ORPHAN_STATE_LOST")
                    report["durable_orphan_seen"] = True
                    outcome = ledger.reconcile(run_id)
                    report["recovery_result"] = outcome["result"]
                    if (outcome["result"] != "INTERRUPTED_CLEAN" or
                            outcome["run"]["result_code"] != "RECOVERED_ORPHAN" or
                            ledger.transitions(run_id)[-2:] != [
                                "RECOVERY_BLOCKED", "INTERRUPTED_CLEAN"]):
                        raise RecoveryProbeBlocked("RECOVERY_NOT_CLEAN")
                    absent = fixed(
                        ["podman", "container", "exists", managed_container(run_id)],
                        20)
                    report["container_removed"] = absent.returncode == 1
                    if not report["container_removed"]:
                        raise RecoveryProbeBlocked("CONTAINER_REMAINS")
                    try:
                        ledger.start(REQUEST_ID)
                    except DuplicateRequest as duplicate:
                        report["duplicate_request_denied"] = (
                            duplicate.record["run_id"] == run_id and
                            len(ledger.history()) == 1)
                    if not report["duplicate_request_denied"]:
                        raise RecoveryProbeBlocked("IDEMPOTENCY_FAILED")
                    report["repeated_recovery_safe"] = (
                        ledger.reconcile(run_id)["result"] == "ALREADY_TERMINAL")
                    if not report["repeated_recovery_safe"]:
                        raise RecoveryProbeBlocked("RECOVERY_NOT_CLEAN")
                report["status"] = "REAL_RECOVERY_PASS"
            finally:
                if process.poll() is None:
                    # Own coordinator PID only; not its Podman guest.
                    process.kill()
                    process.wait(timeout=15)
                if process.stdout is not None:
                    process.stdout.close()
                if process.stdin is not None and not process.stdin.closed:
                    process.stdin.close()
                # Even if the probe failed before the READY line, recover
                # registered container(s) through the same label-checking
                # API. Never blindly remove by name or kill other guests.
                if directory.is_dir():
                    with RunLedger(directory) as cleanup:
                        for record in cleanup.history(limit=5):
                            if record["state"] not in (
                                "SUCCEEDED", "FAILED", "DECLINED",
                                "INTERRUPTED_CLEAN"):
                                outcome = cleanup.reconcile(record["run_id"])
                                if outcome["result"] == "RECOVERY_BLOCKED":
                                    report["status"] = "BLOCKED"
                                    report["reason"] = "RECOVERY_CLEANUP_BLOCKED"
    except RecoveryProbeBlocked as error:
        report["reason"] = error.code
    except (OSError, ValueError, RuntimeError,
            subprocess.TimeoutExpired) as error:
        # Only the exception type is permitted into sanitized evidence.
        report["reason"] = type(error).__name__
    return report


def main() -> int:
    if len(sys.argv) == 4 and sys.argv[1] == "--child":
        # Parent alone selects this internal mode and fixed image.
        image_id = sys.argv[3]
        if image_id != OWNER_IMAGE_ID:
            return 2
        _child(pathlib.Path(sys.argv[2]), image_id)
        return 2
    if len(sys.argv) != 1:
        print("Usage: python3 tools/m1/real_recovery_probe.py")
        return 2
    report = run_probe()
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    evidence = ARTIFACTS / ("m114-recovery-" + uuid.uuid4().hex[:12] + ".json")
    fd = os.open(evidence, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2, sort_keys=True)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    print("M1.14", report["status"])
    print("  owner still active was protected:", report["live_owner_protected"])
    print("  registered container label verified:", report["label_verified"])
    print("  coordinator hard-exit verified:", report["crash_exit_verified"])
    print("  durable RUNNING orphan recovered:", report["durable_orphan_seen"])
    print("  recovery result:", report["recovery_result"])
    print("  orphan container removed:", report["container_removed"])
    print("  repeated request refused:", report["duplicate_request_denied"])
    print("  safe repeated reconciliation:", report["repeated_recovery_safe"])
    print("  ACP/coding/model/network: NOT_REQUESTED")
    if report["reason"]:
        print("  blocked:", report["reason"])
    print("Evidence:", evidence)
    return 0 if report["status"] == "REAL_RECOVERY_PASS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
