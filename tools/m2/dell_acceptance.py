#!/usr/bin/env python3
"""Dell M2→M1 fixed-fixture acceptance; owner approval required for real coding.

This is an operator-invoked local test, NOT a CI step or autonomous scheduler.
It runs trusted model-free rootless Podman negative probes, prepares the exact
offline image, launches the loopback .NET API, and creates a disposable repair
task through the public API. It cannot run an agent until the owner types the
FULL exact executable M1 hash. It never accesses arbitrary owner repositories.
"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import re
import secrets
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[2]
DEFAULT_API = pathlib.Path.home() / ".local/opt/codingassistant-api/CodingAssistant.Api"
FIXTURE = "02-build-repair"
STATES_FINISHED = frozenset((
    "Succeeded", "Failed", "Interrupted", "Cancelled", "RecoveryBlocked",
))
ID = re.compile(r"[a-f0-9]{32}\Z")
HASH = re.compile(r"[a-f0-9]{64}\Z")


class AcceptanceBlocked(RuntimeError):
    pass


def fixed_local_command(command: list[str], timeout: int) -> str:
    """Execute only a reviewed local script via argv; never a shell."""
    outcome = subprocess.run(
        command, cwd=ROOT, text=True, input="", capture_output=True,
        timeout=timeout, check=False)
    if outcome.returncode:
        raise AcceptanceBlocked(
            "Mandatory local qualification step failed: " + pathlib.Path(command[1]).name
            + " (exit " + str(outcome.returncode) + ").")
    if len(outcome.stdout) > 20000:
        raise AcceptanceBlocked("Unexpectedly large bounded verification output")
    return outcome.stdout


def preflight() -> None:
    if not sys.platform.startswith("linux") or os.geteuid() == 0:
        raise AcceptanceBlocked("Acceptance requires non-root Linux with rootless Podman")
    for relative in (
        "tools/m1/os_negative_boundary_probe.py",
        "tools/m1/scoped_worktree_probe.py",
    ):
        target = ROOT / relative
        if not target.is_file() or target.is_symlink():
            raise AcceptanceBlocked("Required independently reviewed security probe missing")
        print("Security-negative prerequisite:", relative, flush=True)
        output = fixed_local_command([sys.executable, str(target)], 360)
        if not ("PASS" in output and "BLOCKED" not in output):
            raise AcceptanceBlocked("Security-negative evidence did not report PASS")
        print("  PASS — rootless, model-free boundary probe", flush=True)
    print("Preparing exact immutable M1 derivative OFFLINE (no model run).", flush=True)
    fixed_local_command([
        sys.executable,
        str(ROOT / "tools/m1/approved_gateway_coding.py"),
        "--prepare-only",
    ], 1200)
    print("Offline image and pinned model provenance preflight: PASS", flush=True)


class ApiClient:
    def __init__(self, port: int, token: str):
        self.origin = f"http://127.0.0.1:{port}"
        self.token = token
        # Even if the operator has HTTP_PROXY configured, the owner bearer
        # must never be sent to a proxy; this client is loopback-only.
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def call(self, method: str, path: str, body: dict | None = None,
             timeout: int = 40) -> dict | list:
        if not path.startswith("/") or "//" in path or ".." in path:
            raise AcceptanceBlocked("Refusing non-canonical local endpoint")
        data = None if body is None else json.dumps(body, separators=(",", ":")).encode()
        request = urllib.request.Request(
            self.origin + path, method=method, data=data,
            headers={
                "Authorization": "Bearer " + self.token,
                "Content-Type": "application/json",
            })
        try:
            with self.opener.open(request, timeout=timeout) as response:
                if response.status not in (200, 201, 202):
                    raise AcceptanceBlocked("Unexpected local API response category")
                raw = response.read(120_000)
                if len(raw) >= 120_000:
                    raise AcceptanceBlocked("Unbounded local API response")
                return json.loads(raw)
        except urllib.error.HTTPError as exc:
            raise AcceptanceBlocked(
                f"Local control plane rejected {method} {path}: HTTP {exc.code}"
            ) from None
        except (urllib.error.URLError, TimeoutError, ValueError):
            raise AcceptanceBlocked("Local control plane unavailable or malformed") from None


def api_task(client: ApiClient) -> tuple[str, str, str]:
    request_id = secrets.token_hex(16)
    task = client.call("POST", "/api/tasks", {
        "title": "Dell qualified disposable C# repair",
        "objective": "Fix the known compiler errors in the disposable 02-build-repair fixture.",
        "requestId": request_id,
    })
    task_id = task["id"]
    if not ID.fullmatch(task_id):
        raise AcceptanceBlocked("Invalid task identity")
    draft = client.call("POST", f"/api/tasks/{task_id}/plans", {
        "summary": "Run ONLY fixed, isolated, independently evaluated C# repair.",
        "expectedVersion": 0,
    })
    m2_hash = draft["planHash"]
    if not HASH.fullmatch(m2_hash):
        raise AcceptanceBlocked("Invalid M2 planning hash")
    client.call("POST", f"/api/tasks/{task_id}/approvals", {
        "version": 1, "approval": "APPROVE " + m2_hash,
    })
    intent = client.call("POST", f"/api/tasks/{task_id}/runs", {
        "planVersion": 1, "planHash": m2_hash,
        "requestId": secrets.token_hex(16),
    })
    run_id = intent["id"]
    if not ID.fullmatch(run_id) or intent["state"] != "AwaitingExecutionAuthorization":
        raise AcceptanceBlocked("Unexpected execution intent state")
    return task_id, run_id, m2_hash


def exact_owner_approval(client: ApiClient, run_id: str, control_hash: str) -> str:
    view = client.call("POST", f"/api/runs/{run_id}/execution-plan", {}, timeout=180)
    execution_hash = view["planHash"]
    plan = view["plan"]
    if (not HASH.fullmatch(execution_hash) or execution_hash == control_hash or
            view["state"] != "Previewed" or
            plan["fixture"] != FIXTURE or
            plan["model"] != "qwen3.5:4b" or
            plan["writable_files"] != ["api/Program.cs"] or
            plan["no_cloud"] is not True or
            plan["no_git_remote"] is not True or
            plan["control"]["run_id"] != run_id or
            plan["control"]["plan_hash"] != control_hash):
        raise AcceptanceBlocked("Unexpected exact executable plan — no agent admitted")

    print("\nEXACT OWNER REVIEW — this is a REAL local model coding run.")
    print("  Task: fixed disposable C# compiler repair")
    print("  Permitted write: api/Program.cs (disposable worktree only)")
    print("  Model: qwen3.5:4b")
    print("  Model digest:", plan["model_digest"])
    print("  Immutable coding image:", plan["image_id"])
    print("  Immutable evaluator:", plan["evaluator_image_id"])
    print("  External network/Git remotes: DISABLED")
    print("  M2 planning hash:", control_hash)
    print("  M1 executable hash:", execution_hash)
    print("\nType the FULL line below to authorize real local coding:")
    print("APPROVE " + execution_hash)
    try:
        typed = input("> ").strip()
    except EOFError:
        raise AcceptanceBlocked("No interactive owner approval — no model executed") from None
    if typed != "APPROVE " + execution_hash:
        raise AcceptanceBlocked("Exact owner approval withheld — no model executed")

    authorized = client.call(
        "POST", f"/api/runs/{run_id}/execution-authorization",
        {"approval": typed})
    if authorized["state"] != "Authorized" or authorized["planHash"] != execution_hash:
        raise AcceptanceBlocked("Exact executable authorization not durable")
    return execution_hash


def monitor(client: ApiClient, run_id: str) -> str:
    cursor = 0
    last = None
    verified_event_seen = False
    while True:
        page = client.call("GET", f"/api/runs/{run_id}/events?after={cursor}&limit=100")
        for event in page["events"]:
            if (event["runId"] != run_id or event["sequence"] != cursor + 1 or
                    not re.fullmatch(r"[A-Za-z]{4,40}", event["kind"])):
                raise AcceptanceBlocked("Durable event replay integrity mismatch")
            cursor += 1
            if event["kind"] == "ExecutionVerified":
                verified_event_seen = True
            print(f"  event {cursor}: {event['kind']} / {event['state']}", flush=True)
        run = client.call("GET", f"/api/runs/{run_id}")
        state = run["state"]
        if state != last:
            print("  run state:", state, flush=True)
            last = state
        if state in STATES_FINISHED:
            if state == "Succeeded" and (
                    not verified_event_seen or cursor != run["lastSequence"]):
                raise AcceptanceBlocked(
                    "Success lacks complete durable independent verification evidence")
            return state
        time.sleep(2)


def pick_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    if port < 1024:
        raise AcceptanceBlocked("Invalid user-owned loopback port")
    return port


def perform_run(binary: pathlib.Path) -> None:
    if binary.is_symlink() or not binary.is_file():
        raise AcceptanceBlocked(
            "Self-contained CI API binary not installed at " + str(binary))
    port = pick_port()
    token = secrets.token_hex(32)
    data_dir = ROOT / "artifacts/m2"
    data_dir.mkdir(parents=True, exist_ok=True)
    private = data_dir / ("qualification-" + secrets.token_hex(8))
    private.mkdir(mode=0o700)
    env = os.environ.copy()
    env.update({
        "CODINGASSISTANT_OWNER_TOKEN": token,
        "CODINGASSISTANT_ENABLE_LOCAL_EXECUTION": "1",
        "CODINGASSISTANT_REPOSITORY_ROOT": str(ROOT),
        "CODINGASSISTANT_DATA_DIR": str(private),
        "CODINGASSISTANT_PORT": str(port),
    })
    client = ApiClient(port, token)
    print("Starting authenticated loopback API from local self-contained build.")
    process = subprocess.Popen(
        [str(binary)], cwd=ROOT, env=env,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    run_id = None
    try:
        for _ in range(100):
            if process.poll() is not None:
                raise AcceptanceBlocked("Local API failed before startup")
            try:
                status = client.call("GET", "/api/status", timeout=2)
                if status.get("executionEnabled") is True:
                    break
                raise AcceptanceBlocked("Local M1 execution not enabled")
            except AcceptanceBlocked:
                time.sleep(.1)
        else:
            raise AcceptanceBlocked("Local API did not become ready")

        _, run_id, control_hash = api_task(client)
        exact_owner_approval(client, run_id, control_hash)
        started = client.call("POST", f"/api/runs/{run_id}/start", {})
        if started["state"] not in ("Starting", "Running"):
            raise AcceptanceBlocked("Execution was not durably admitted")
        print("One exact owner-approved M1 worker admitted.", flush=True)
        final = monitor(client, run_id)
        print("\nM2→M1 final durable state:", final)
        print("Private API evidence directory:", private)
        if final != "Succeeded":
            raise AcceptanceBlocked(
                "M1 is NOT GREEN; inspect private run ledger and require cleanup")
        print("M2→M1 POSITIVE DELl ACCEPTANCE: VERIFIED (requires separate security negatives).")
    except KeyboardInterrupt:
        if run_id is not None:
            print("\nCancellation requested — no automatic replay.")
            try:
                client.call("POST", f"/api/runs/{run_id}/execution-cancel", {})
                print("Cancellation was requested; awaiting bounded cleanup.")
                final_state = None
                for _ in range(90):
                    record = client.call("GET", f"/api/runs/{run_id}")
                    final_state = record["state"]
                    if final_state in STATES_FINISHED:
                        break
                    time.sleep(1)
                print("Cancellation/recovery state:", final_state)
                if final_state not in STATES_FINISHED:
                    print("Cleanup unconfirmed — no new coding is authorized.")
            except AcceptanceBlocked:
                print("Cancellation unconfirmed — do not start new work; reconcile M1.")
        raise AcceptanceBlocked("Operator interrupted acceptance") from None
    finally:
        # Stop ONLY the API server spawned here. Never kill unrelated processes.
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--preflight-only", action="store_true",
                      help="Run model-free negative probes and offline image prep only")
    mode.add_argument("--run", action="store_true",
                      help="Also require exact interactive approval and run one coding task")
    parser.add_argument("--api-binary", type=pathlib.Path, default=DEFAULT_API,
                        help="Path to local CI-built self-contained Linux API binary")
    args = parser.parse_args()
    try:
        preflight()
        if args.run:
            perform_run(args.api_binary.expanduser())
        else:
            print("M2→M1 MODEL-FREE PREFLIGHT_PASS; no agent/model started.")
        return 0
    except (AcceptanceBlocked, OSError, subprocess.TimeoutExpired) as exc:
        print("M2→M1 ACCEPTANCE BLOCKED:", exc, file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
