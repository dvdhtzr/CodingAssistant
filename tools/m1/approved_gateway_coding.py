#!/usr/bin/env python3
"""M1.12: one owner-approved disposable C# repair through REAL Gateway ACPX/OpenCode.

Fixed source fixture and pinned local Qwen only. This is not a general-purpose
autonomous runner: no source repo mount, Git remote, cloud inference or arbitrary
prompt. All model execution runs in rootless no-network Podman; only two local
mounts are a fresh disposable C# COPY and the private, allowlisted Unix socket.
A separate no-network evaluator checks the results after the Gateway is removed.

--prepare opts into an offline copy-only local image build. Actual coding also
requires typing APPROVE <exact plan SHA256> after the plan is printed. A failed
gate returns BLOCKED/NOT_APPROVED, never a false qualification pass.
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import http.client
import json
import os
import pathlib
import re
import secrets
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/m0"))
from acpx_initialize_probe import OWNER_IMAGE_ID
from acpx_opencode_image_probe import inspect_composed, verify_combined, verify_inputs, ephemeral_gateway
from gateway_image_probe import fixed
from gateway_loopback_probe import NODE_HTTP, parse_codes
from ollama_bridge import BridgeHandler, serve_bridge
from pinned_openai import pinned_show_response, pinned_tags_response, exact_show_request
from pinned_inference import verify_model_digest
from real_qualification import pin_model, scan_workspace
from qualification_isolation_probe import check_manifest, sealed_snapshot, FIXTURES
from qualification_acceptance_probe import run_evaluator, IMAGE as EVAL_TAG
from sandbox_probe import image_status, inspect_podman

ARTIFACTS = ROOT / "artifacts/m1"
CONTEXT = ROOT / "infra/m1"
CONTAINERFILE = CONTEXT / "Containerfile.coding-turn"
CONFIG = CONTEXT / "coding-config.json"
LAUNCHER = CONTEXT / "coding-runner.mjs"
PLUGIN = CONTEXT / "coding-plugin"
SOURCES = (CONTAINERFILE, CONFIG, LAUNCHER, PLUGIN / "index.mjs",
           PLUGIN / "openclaw.plugin.json", PLUGIN / "package.json",
           pathlib.Path(__file__).resolve())
IMAGE_TAG = "localhost/codingassistant-m112-approved-coding:2026.11.5"
METHOD = "codingassistant.coding.run"
TASK = "02-build-repair"
MODEL = "qwen3.5:4b"
HEX64 = re.compile(r"(?:sha256:)?([a-f0-9]{64})\Z")
LABEL = "io.github.dvdhtzr.codingassistant.m112"
MAX_CHAT = 262144
MAX_RESPONSE = 8 * 1024 * 1024
MAX_REQUESTS = 10
EXPECTED_RPC = {
    "backend": "acpx", "turnCompleted": True,
    "promptSent": True, "sessionCreated": True,
    "modelVerifiedByHost": False,
    "modelInferenceClaim": "host-broker-verification-required",
}

def fingerprint() -> str:
    digest = hashlib.sha256()
    for file in SOURCES:
        if not file.is_file() or file.is_symlink():
            raise ValueError("Untrusted coding slice source")
        data = file.read_bytes()
        if not 0 < len(data) < 100_000:
            raise ValueError("Coding slice source outside size bound")
        digest.update(file.relative_to(ROOT).as_posix().encode() + b"\0" + data + b"\0")
    config = json.loads(CONFIG.read_text(encoding="utf-8"))
    if config.get("acp") != {
            "enabled": True, "backend": "acpx", "allowedAgents": ["opencode"]}:
        raise ValueError("Unapproved ACP execution configuration")
    acpx = config["plugins"]["entries"]["acpx"]["config"]
    if (acpx["cwd"] != "/workspace" or acpx["probeAgent"] != "opencode" or
            acpx["permissionMode"] != "approve-all" or
            acpx["nonInteractivePermissions"] != "deny" or
            acpx["agentProcessEnvAllow"] != [
                "OPENCODE_CONFIG_CONTENT", "OPENCODE_DISABLE_AUTOUPDATE", "CI"] or
            acpx["agents"] != {
                "opencode": {"command": "/usr/local/bin/opencode", "args": ["acp"]}} or
            acpx["mcpServers"] != {} or
            acpx["pluginToolsMcpBridge"] is not False or
            acpx["openClawToolsMcpBridge"] is not False):
        raise ValueError("Approved coding configuration changed")
    source = (PLUGIN / "index.mjs").read_text(encoding="utf-8")
    if (source.count("api.registerGatewayMethod(") != 1 or
            '"operator.write"' not in source or
            'getAcpRuntimeBackend("acpx")' not in source or
            "runtime.ensureSession(" not in source or
            "runtime.startTurn(" not in source):
        raise ValueError("Pinned Gateway coding interface changed")
    docker = CONTAINERFILE.read_text()
    if any(line.lstrip().startswith(("RUN ", "ADD ")) for line in docker.splitlines()):
        raise ValueError("No executable/download image build steps authorized")
    return digest.hexdigest()

def image_build_command(source_hash: str) -> list[str]:
    if not HEX64.fullmatch(source_hash):
        raise ValueError("Invalid source hash")
    return ["podman", "build", "--pull=never", "--network=none",
            "--file", str(CONTAINERFILE), "--tag", IMAGE_TAG,
            "--build-arg", "BASE_IMAGE=" + OWNER_IMAGE_ID,
            "--label", LABEL + ".base=" + OWNER_IMAGE_ID,
            "--label", LABEL + ".sources=" + source_hash,
            "--label", LABEL + ".method=" + METHOD, str(CONTEXT)]

def inspect_image(source_hash: str) -> str | None:
    result = fixed(["podman", "image", "inspect", IMAGE_TAG], timeout=25)
    if result.returncode != 0:
        return None
    raw = json.loads(result.stdout)
    if type(raw) is not list or len(raw) != 1 or type(raw[0]) is not dict:
        raise ValueError("Image provenance unavailable")
    item = raw[0]
    match = HEX64.fullmatch(item.get("Id", ""))
    labels = item.get("Labels")
    if (match is None or match.group(1) == OWNER_IMAGE_ID or
            type(labels) is not dict or
            labels.get(LABEL + ".base") != OWNER_IMAGE_ID or
            labels.get(LABEL + ".sources") != source_hash or
            labels.get(LABEL + ".method") != METHOD or
            IMAGE_TAG not in item.get("RepoTags", [])):
        raise ValueError("Unexpected derived image provenance")
    return match.group(1)

def build_approved_plan(base_hash: str, image_id: str, model_digest: str,
                        manifest_hash: str, evaluator_id: str
                        ) -> tuple[dict[str, object], str]:
    for value in (base_hash, image_id, model_digest, manifest_hash, evaluator_id):
        if HEX64.fullmatch(value) is None:
            raise ValueError("Incomplete plan provenance")
    plan = {"schema_version": 1, "stage": "M1.12",
            "assignment": "repair fixed C# compiler errors in disposable api/Program.cs",
            "fixture": TASK, "fixture_sha256": base_hash,
            "manifest_sha256": manifest_hash, "model": MODEL,
            "model_digest": model_digest, "image_id": image_id,
            "evaluator_image_id": evaluator_id,
            "source_sha256": fingerprint(),
            "writable_files": ["api/Program.cs"],
            "gateway_method": METHOD, "no_cloud": True, "no_git_remote": True}
    raw = json.dumps(plan, sort_keys=True, separators=(",", ":")).encode()
    return plan, hashlib.sha256(raw).hexdigest()

def broker_for(model: str, digest: str, state: dict[str, int]) -> type[BridgeHandler]:
    """Limited inference broker: one fixed local Qwen; never generic HTTP proxy."""
    lock = threading.Lock()
    class Handler(BridgeHandler):
        def do_GET(self) -> None:
            if self.path not in ("/v1/models", "/api/tags") or (
                    self.headers.get("Content-Length") or
                    self.headers.get("Transfer-Encoding")):
                with lock: state["denied"] += 1
                self._reply(403, b'{"error":"denied"}')
                return
            try:
                if not verify_model_digest(model, digest):
                    raise ValueError("Model drift")
                data = (pinned_tags_response(model, digest) if self.path == "/api/tags"
                        else json.dumps({"object": "list", "data": [
                            {"id": model, "object": "model", "owned_by": "ollama"}
                        ]}, separators=(",", ":")).encode())
                self._reply(200, data)
            except (OSError, ValueError, TypeError, RuntimeError):
                self._reply(502, b'{"error":"model unavailable"}')

        def do_POST(self) -> None:
            show = self.path == "/api/show"
            chat = self.path == "/v1/chat/completions"
            limit = 256 if show else MAX_CHAT
            length = self.headers.get("Content-Length", "")
            if (not (show or chat) or not length.isascii() or not length.isdecimal()
                    or not 0 < int(length) <= limit or
                    self.headers.get("Content-Type", "").split(";")[0].lower() != "application/json"
                    or self.headers.get("Transfer-Encoding")):
                with lock: state["denied"] += 1
                self._reply(403, b'{"error":"denied"}')
                return
            body = self.rfile.read(int(length))
            if show:
                if not exact_show_request(body, model):
                    with lock: state["denied"] += 1
                    self._reply(403, b'{"error":"denied"}')
                    return
                try:
                    self._reply(200, pinned_show_response(model, digest))
                except (OSError, ValueError, TypeError, RuntimeError, http.client.HTTPException):
                    self._reply(502, b'{"error":"profile unavailable"}')
                return
            try:
                req = json.loads(body)
                if (type(req) is not dict or req.get("model") != model or
                        req.get("stream") is not True or
                        not isinstance(req.get("messages"), list) or
                        not req["messages"]):
                    raise ValueError("Wrong model/protocol")
            except (ValueError, TypeError):
                with lock: state["denied"] += 1
                self._reply(403, b'{"error":"denied"}')
                return
            with lock:
                if state["attempts"] >= MAX_REQUESTS:
                    state["denied"] += 1
                    self._reply(403, b'{"error":"budget exceeded"}')
                    return
                state["attempts"] += 1
            conn = None
            try:
                if not verify_model_digest(model, digest):
                    raise ValueError("Model drift")
                conn = http.client.HTTPConnection("127.0.0.1", 11434, timeout=780)
                conn.request("POST", "/v1/chat/completions", body=body, headers={
                    "Content-Type": "application/json", "Connection": "close"})
                response = conn.getresponse()
                if response.status != 200:
                    raise ValueError("Pinned provider rejected request")
                data = response.read(MAX_RESPONSE + 1)
                if len(data) > MAX_RESPONSE or b"data: [DONE]" not in data:
                    raise ValueError("Incomplete model SSE")
                verified_events = 0
                for part in data.splitlines():
                    if not part.startswith(b"data: ") or part == b"data: [DONE]":
                        continue
                    event = json.loads(part[6:])
                    if event.get("model") != model:
                        raise ValueError("Provider response model mismatch")
                    verified_events += 1
                if not verified_events or not verify_model_digest(model, digest):
                    raise ValueError("Unverified local model response")
                with lock: state["verified"] += 1
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Content-Length", str(len(data)))
                self.send_header("Cache-Control", "no-store")
                self.send_header("Connection", "close")
                self.end_headers()
                self.wfile.write(data)
                self.close_connection = True
            except (OSError, ValueError, TypeError, RuntimeError,
                    http.client.HTTPException, json.JSONDecodeError):
                with lock: state["failed"] += 1
                self._reply(502, b'{"error":"local model request failed"}')
            finally:
                if conn: conn.close()
    return Handler

def container_command(image_id: str, name: str, env: pathlib.Path,
                      workspace: pathlib.Path, socket_dir: pathlib.Path) -> list[str]:
    if (HEX64.fullmatch(image_id) is None or workspace.is_symlink() or
            not workspace.is_dir() or socket_dir.is_symlink() or
            not (socket_dir / "ollama.sock").exists()):
        raise ValueError("Invalid pinned image, worktree or IPC mount")
    args = ephemeral_gateway(image_id, name, env)
    at = args.index(image_id)
    args[at:at] = [
        "--volume", str(workspace.resolve()) + ":/workspace:rw,nosuid,nodev",
        "--volume", str(socket_dir.resolve()) + ":/run/codingassistant:ro,nosuid,nodev",
        "--env=OPENCLAW_CONFIG_PATH=/app/codingassistant-coding-config.json",
    ]
    args[args.index("--memory=2g")] = "--memory=3g"
    args[args.index("--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup")] = (
        "--tmpfs=/tmp:rw,nosuid,nodev,size=768m,notmpcopyup")
    start = args.index(image_id)
    args[start + 1:] = ["/app/codingassistant-coding-runner.mjs"]
    return args

def gateway_rpc_command(name: str, plan_hash: str) -> list[str]:
    if not re.fullmatch(r"ca-m17-acpx-[0-9a-f]{12}", name) or (
            HEX64.fullmatch(plan_hash) is None):
        raise ValueError("Invalid isolated Gateway request")
    params = json.dumps({"planHash": plan_hash}, separators=(",", ":"))
    return ["podman", "exec", "--interactive=false", name, "node",
            "/app/openclaw.mjs", "gateway", "call", METHOD,
            "--params", params, "--json", "--timeout", "900000"]

def parse_rpc(raw: str) -> dict[str, object]:
    if not 0 < len(raw) < 4000:
        raise ValueError("Missing/oversized Gateway coding RPC response")
    item = json.loads(raw)
    if (type(item) is not dict or
            set(item) != set(EXPECTED_RPC) | {"eventsObserved", "toolCallsObserved"} or
            any(type(item.get(k)) is not type(v) or item[k] != v
                for k, v in EXPECTED_RPC.items()) or
            type(item["eventsObserved"]) is not int or
            type(item["toolCallsObserved"]) is not int or
            not 0 < item["eventsObserved"] <= 2500 or
            not 0 <= item["toolCallsObserved"] <= item["eventsObserved"]):
        raise ValueError("Gateway ACPX coding turn did not complete")
    return {"backend": "acpx", "session_created": True, "prompt_sent": True,
            "turn_completed": True, "events": item["eventsObserved"],
            "tool_calls": item["toolCallsObserved"]}

def run_gateway(image: str, worktree: pathlib.Path, socket_dir: pathlib.Path,
                plan_hash: str) -> dict[str, object]:
    name = "ca-m17-acpx-" + secrets.token_hex(6)
    status: dict[str, object] = {"health": None, "rpc": None, "removed": False}
    with tempfile.TemporaryDirectory(prefix="ca-m112-credentials-") as temporary:
        env = pathlib.Path(temporary) / "gateway.env"
        descriptor = os.open(env, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8") as out:
            out.write("OPENCLAW_GATEWAY_TOKEN=" + secrets.token_hex(32) + "\n")
            out.write("CA_M112_PLAN_HASH=" + plan_hash + "\n")
            out.write("OPENCLAW_SKIP_CHANNELS=1\n")
            out.write("OPENCLAW_SKIP_CANVAS_HOST=1\n")
        try:
            result = fixed(container_command(image, name, env, worktree, socket_dir), timeout=45)
            if result.returncode != 0:
                raise ValueError("Isolated coding Gateway failed to launch")
            for _ in range(50):
                alive = fixed(["podman", "inspect", "--format",
                               "{{.State.Running}}", name], timeout=12)
                if alive.returncode != 0 or alive.stdout.strip() != "true":
                    raise ValueError("Isolated coding Gateway exited")
                probe = fixed(["podman", "exec", "--interactive=false",
                               name, "node", "-e", NODE_HTTP], timeout=25)
                if probe.returncode != 0:
                    raise ValueError("Gateway readiness probe failed")
                codes = parse_codes(probe.stdout)
                status["health"] = codes
                if all(codes[k] == 200 for k in ("/healthz", "/startupz", "/readyz")):
                    break
                time.sleep(2)
            else:
                raise ValueError("Isolated Gateway never became ready")
            call = fixed(gateway_rpc_command(name, plan_hash), timeout=950)
            if call.returncode != 0:
                raise ValueError("Registered ACPX coding turn rejected or failed")
            status["rpc"] = parse_rpc(call.stdout)
        finally:
            try:
                fixed(["podman", "rm", "-f", name], timeout=30)
                check = fixed(["podman", "container", "exists", name], timeout=15)
                status["removed"] = check.returncode == 1
            except (OSError, subprocess.TimeoutExpired):
                status["removed"] = False
            if not status["removed"]:
                raise ValueError("Isolated coding Gateway cleanup failed")
    return status

def execute(prepare: bool) -> dict[str, object]:
    report: dict[str, object] = {
        "schema_version": 1, "stage": "M1.12-approved-gateway-coding",
        "utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "BLOCKED", "reason_code": None, "approved": False,
        "plan_hash": None, "base_image": OWNER_IMAGE_ID,
        "derived_image": None, "model": MODEL, "model_digest": None,
        "gateway": None, "broker": None, "source_before": None,
        "source_after": None, "evaluator": None, "fixture_unchanged": None,
        "sentinel_unchanged": None, "inference_separate_from_evaluator": True,
        "original_repo_mounted": False, "git_remote_enabled": False,
        "external_network_enabled": False,
    }
    try:
        if sys.platform != "linux" or os.geteuid() == 0:
            raise ValueError("Must use Linux rootless Podman")
        if inspect_podman(20).get("rootless") is not True:
            raise ValueError("Rootless Podman not available")
        original = verify_inputs()
        base_image = inspect_composed(original)
        if base_image != OWNER_IMAGE_ID or (
                verify_combined(base_image)["registry"] != {
                    "id": "opencode", "installation": "installed",
                    "expectedCommand": True}):
            raise ValueError("M1.7 pinned composition changed")
        manifest_hash = check_manifest()
        base_hash = sealed_snapshot(FIXTURES / TASK, TASK)
        eval_img = image_status(EVAL_TAG, 25)
        if not eval_img.get("present") or not eval_img.get("image_id"):
            raise ValueError("Independent offline evaluator image unavailable")
        digest = pin_model(MODEL)
        report["model_digest"] = digest
        source_hash = fingerprint()
        if prepare:
            if shutil.disk_usage(ROOT).free < 2 * 1024**3:
                raise ValueError("Insufficient disk for offline coding layer")
            ARTIFACTS.mkdir(parents=True, exist_ok=True)
            log = ARTIFACTS / ("m112-build-" + uuid.uuid4().hex[:12] + ".log")
            with log.open("x", encoding="utf-8") as stream:
                built = subprocess.run(image_build_command(source_hash),
                                       stdin=subprocess.DEVNULL,
                                       stdout=stream, stderr=subprocess.STDOUT,
                                       timeout=600, check=False)
            if built.returncode != 0:
                raise ValueError("Offline derived image build failed")
        image = inspect_image(source_hash)
        if image is None:
            raise ValueError("Derived image absent; opt in with --prepare")
        report["derived_image"] = image
        plan, plan_hash = build_approved_plan(
            base_hash, image, digest, manifest_hash, eval_img["image_id"])
        report["plan_hash"] = plan_hash
        print("Approved-scope preview:", plan["assignment"])
        print("  fixture:", TASK, "model:", MODEL)
        print("  disposable writable file:", "api/Program.cs")
        print("  exact plan SHA256:", plan_hash)
        try:
            response = input("Type APPROVE <64-character plan hash> to run real isolated coding: ")
        except EOFError:
            response = ""
        if response.strip() != "APPROVE " + plan_hash:
            report["status"] = "NOT_APPROVED"
            return report
        report["approved"] = True

        with tempfile.TemporaryDirectory(prefix="ca-m112-isolated-") as temporary:
            root = pathlib.Path(temporary)
            project = root / "approved"
            shutil.copytree(FIXTURES / TASK, project)
            root.chmod(0o700)
            sibling = root / "unmounted.txt"
            sibling.write_text("UNMOUNTED-DO-NOT-TOUCH", encoding="utf-8")
            report["source_before"] = scan_workspace(project, TASK)
            ipc = root / "ipc"
            ipc.mkdir(mode=0o700)
            broker_state = {"attempts": 0, "verified": 0, "failed": 0, "denied": 0}
            with serve_bridge(ipc / "ollama.sock",
                    broker_for(MODEL, digest, broker_state)):
                gateway = run_gateway(image, project, ipc, plan_hash)
            report["gateway"] = gateway
            report["broker"] = dict(broker_state)
            report["source_after"] = scan_workspace(project, TASK)
            report["fixture_unchanged"] = (
                sealed_snapshot(FIXTURES / TASK, TASK) == base_hash)
            report["sentinel_unchanged"] = (
                sibling.read_text(encoding="utf-8") == "UNMOUNTED-DO-NOT-TOUCH")
            old = report["source_before"]
            new = report["source_after"]
            if (not gateway["removed"] or not report["fixture_unchanged"] or
                    not report["sentinel_unchanged"] or
                    broker_state["verified"] < 1 or broker_state["denied"] != 0 or
                    broker_state["failed"] != 0 or
                    old.get("api/Program.cs") == new.get("api/Program.cs") or
                    old.get("api/Repair.csproj") != new.get("api/Repair.csproj")):
                raise ValueError("Integrated model/boundary/source gate failed")
            # Isolated verification: no host Ollama socket is mounted here.
            eval_dir = root / "offline-evaluation"
            result = run_evaluator(
                eval_img["image_id"], TASK, project, sibling,
                eval_dir, timeout=480)
            # Never retain arbitrary stderr or raw model responses.
            report["evaluator"] = {
                "status": result["status"], "verified": result["verified"]}
            report["fixture_unchanged"] = (
                sealed_snapshot(FIXTURES / TASK, TASK) == base_hash)
            if not result["verified"] or not report["fixture_unchanged"]:
                raise ValueError("Independent offline acceptance failed")
            post_evaluator_sources = scan_workspace(
                project, TASK, allow_build_outputs=True)
            if post_evaluator_sources != report["source_after"]:
                raise ValueError("Offline evaluator changed approved source")
            if sibling.read_text(encoding="utf-8") != "UNMOUNTED-DO-NOT-TOUCH":
                raise ValueError("Independent evaluator touched unmounted sibling")
            report["status"] = "GATEWAY_APPROVED_CODING_PASS"
    except (OSError, RuntimeError, ValueError, TypeError, KeyError,
            AssertionError, subprocess.TimeoutExpired,
            http.client.HTTPException) as exc:
        report["reason_code"] = type(exc).__name__
    return report

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare", action="store_true",
                        help="Explicitly build the offline copied image before approval")
    args = parser.parse_args()
    report = execute(prepare=args.prepare)
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    path = ARTIFACTS / ("m112-approved-coding-" + uuid.uuid4().hex[:12] + ".json")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2, sort_keys=True)
        stream.write("\n")
    print("M1.12", report["status"])
    print("  approved:", report["approved"], "plan hash:", report["plan_hash"])
    print("  isolated Gateway / host broker:", report["gateway"], report["broker"])
    print("  independent evaluator:", report["evaluator"])
    if report["reason_code"]:
        print("  stopped safely:", report["reason_code"])
    print("Evidence:", path)
    return 0 if report["status"] == "GATEWAY_APPROVED_CODING_PASS" else 2

if __name__ == "__main__":
    raise SystemExit(main())
