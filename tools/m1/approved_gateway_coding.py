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
import difflib
import hashlib
import hmac
import http.client
import json
import os
import pathlib
import re
import secrets
import sqlite3
import shutil
import stat
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
from run_ledger import RunLedger, DuplicateRequest, managed_container, LABEL as LEDGER_LABEL

ARTIFACTS = ROOT / "artifacts/m1"
LEDGER_DIR = ARTIFACTS / "private-ledger"
CONTEXT = ROOT / "infra/m1"
CONTAINERFILE = CONTEXT / "Containerfile.coding-turn"
CONFIG = CONTEXT / "coding-config.json"
LAUNCHER = CONTEXT / "coding-runner.mjs"
PLUGIN = CONTEXT / "coding-plugin"
SOURCES = (CONTAINERFILE, CONFIG, LAUNCHER, PLUGIN / "index.mjs",
           PLUGIN / "safe-failure.mjs",
           PLUGIN / "openclaw.plugin.json", PLUGIN / "package.json",
           ROOT / "tools/m1/run_ledger.py", pathlib.Path(__file__).resolve())
IMAGE_TAG = "localhost/codingassistant-m112-approved-coding:2026.11.5"
METHOD = "codingassistant.coding.run"
STATUS_METHOD = "codingassistant.coding.status"
SAFE_GATEWAY_PHASES = frozenset((
    "waiting", "approved", "backend_lookup", "session_initialize",
    "session_ready", "turn_start", "turn_stream", "turn_result",
    "session_close", "completed", "failed",
))
SAFE_FAILURE_KINDS = frozenset((
    "OTHER", "MODEL_UNADVERTISED", "MODEL_CAPABILITY_MISSING",
    "MODEL_CATALOG_EMPTY", "PROVIDER_AUTH_REQUIRED",
    "OPENCODE_SERVER_UNAVAILABLE", "AGENT_SPAWN_ENOENT",
    "AGENT_SPAWN_FAILED", "AGENT_STARTUP_FAILED", "AGENT_DISCONNECTED",
    "SESSION_OWNER_INVALID", "SESSION_INIT_FAILED", "ACP_REQUEST_INVALID",
    "ACP_REQUEST_INTERNAL", "FILESYSTEM_DENIED", "FILESYSTEM_MISSING",
    "STARTUP_TIMEOUT", "BACKEND_UNAVAILABLE",
))
SAFE_FAILURE_CODES = frozenset((
    "NONE", "ACP_MODEL_UNSUPPORTED", "ACP_SESSION_INIT_FAILED",
    "ACP_BACKEND_UNAVAILABLE", "ACP_INVALID_RUNTIME_OPTION",
    "AGENT_STARTUP_FAILED", "AGENT_SPAWN_ENOENT", "AGENT_DISCONNECTED",
    "SESSION_OWNER_UNSUPPORTED", "SESSION_OWNER_MIGRATION_REQUIRED",
    "ENOENT", "EACCES", "EPERM", "ETIMEDOUT", "ECONNREFUSED",
    "EPIPE", "ACP_REQUEST_INVALID", "ACP_REQUEST_INTERNAL",
))
SAFE_MODEL_REASONS = frozenset(("missing-capability", "unadvertised-model"))
SAFE_EXIT_SIGNALS = frozenset((
    "SIGKILL", "SIGTERM", "SIGABRT", "SIGSEGV", "SIGBUS", "SIGILL",
    "SIGHUP", "SIGINT",
))
SAFE_SYSTEM_CODES = frozenset((
    "ENOENT", "EACCES", "EPERM", "ENOMEM", "EAGAIN", "EMFILE",
    "ENFILE", "ETIMEDOUT", "ECONNREFUSED", "EPIPE",
))
TASK = "02-build-repair"
MODEL = "qwen3.5:4b"
HEX64 = re.compile(r"(?:sha256:)?([a-f0-9]{64})\Z")
LABEL = "io.github.dvdhtzr.codingassistant.m112"
MAX_CHAT = 262144
MAX_RESPONSE = 8 * 1024 * 1024
MAX_REQUESTS = 10
# Explicitly capped: this container hosts both OpenClaw and Bun/OpenCode.
# The M1.8 standalone ACP test ran with only one Node parent and 96 PIDs.
COMBINED_PIDS_LIMIT = 256
COMBINED_CPU_LIMIT = 2
# All data collected here is numeric kernel cgroup accounting only.
# The script reads no process command lines, env, application files or logs.
CGROUP_PIDS_JS = r"""
const fs = require("node:fs");
const read = path => fs.readFileSync("/sys/fs/cgroup/" + path, "utf8").trim();
const events = read("pids.events").split(/\n/);
const max = events.find(line => /^max [0-9]+$/.test(line));
const limit = read("pids.max");
const peak = read("pids.peak");
if (!max || !/^[0-9]+$/.test(limit) || !/^[0-9]+$/.test(peak)) process.exit(78);
process.stdout.write(JSON.stringify({
  limit: Number(limit), peak: Number(peak),
  max_events: Number(max.slice(4)),
}) + "\n");
"""
EXPECTED_RPC = {
    "backend": "acpx", "turnCompleted": True,
    "promptSent": True, "sessionCreated": True,
    "modelVerifiedByHost": False,
    "modelInferenceClaim": "host-broker-verification-required",
}

class SafeGateBlocked(ValueError):
    """Machine-readable bounded failure category, never raw runtime output."""
    def __init__(self, category: str):
        if not re.fullmatch(r"[A-Z_]{4,64}", category):
            raise ValueError("Invalid sanitized failure code")
        self.category = category
        super().__init__(category)


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
    if (source.count("api.registerGatewayMethod(") != 2 or
            '"operator.write"' not in source or
            '"operator.read"' not in source or
            'codingassistant.coding.status' not in source or
            'classifyAcpSessionFailure' not in source or
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
                        manifest_hash: str, evaluator_id: str,
                        control: dict[str, object] | None = None
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
    if control is not None:
        if (type(control) is not dict or
                set(control) != {"task_id", "run_id", "plan_version", "plan_hash"} or
                any(type(control[k]) is not str or
                    re.fullmatch(r"[0-9a-f]{32}", control[k]) is None
                    for k in ("task_id", "run_id")) or
                type(control["plan_version"]) is not int or
                not 1 <= control["plan_version"] <= 1000000 or
                type(control["plan_hash"]) is not str or
                HEX64.fullmatch(control["plan_hash"]) is None):
            raise ValueError("Invalid immutable M2 control-plane binding")
        plan["control"] = control
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
                # Verified, delivered host-side response. No model text,
                # token, tool args or prompts enter the event stream.
                trace_stage("InferenceRequestVerified")
            except (OSError, ValueError, TypeError, RuntimeError,
                    http.client.HTTPException, json.JSONDecodeError):
                with lock: state["failed"] += 1
                self._reply(502, b'{"error":"local model request failed"}')
            finally:
                if conn: conn.close()
    return Handler

def container_command(image_id: str, name: str, env: pathlib.Path,
                      workspace: pathlib.Path, socket_dir: pathlib.Path, *,
                      managed_run_id: str | None = None) -> list[str]:
    if (HEX64.fullmatch(image_id) is None or workspace.is_symlink() or
            not workspace.is_dir() or socket_dir.is_symlink() or
            not (socket_dir / "ollama.sock").exists()):
        raise ValueError("Invalid pinned image, worktree or IPC mount")
    args = ephemeral_gateway(image_id, name, env)
    if managed_run_id is not None:
        if name != managed_container(managed_run_id):
            raise ValueError("Managed container identity mismatch")
        args[args.index(image_id):args.index(image_id)] = [
            "--label", LEDGER_LABEL + "=" + managed_run_id]
    at = args.index(image_id)
    args[at:at] = [
        "--volume", str(workspace.resolve()) + ":/workspace:rw,nosuid,nodev",
        "--volume", str(socket_dir.resolve()) + ":/run/codingassistant:ro,nosuid,nodev",
        "--env=OPENCLAW_CONFIG_PATH=/app/codingassistant-coding-config.json",
        # Mirror M0's independently qualified writable OpenCode directories
        # under the existing, isolated /tmp tmpfs. No additional host mounts.
        "--env=XDG_DATA_HOME=/tmp/.local/share",
        "--env=XDG_STATE_HOME=/tmp/.local/state",
        "--env=XDG_CACHE_HOME=/tmp/.cache",
    ]
    args[args.index("--memory=2g")] = "--memory=3g"
    # Bun may abort on EAGAIN when a shared Node+OpenCode cgroup exhausts
    # its thread/PID cap. Enlarge the bound, never remove it; constrain CPU
    # availability to the same two CPUs used in the successful M1.8 ACP test.
    args[args.index("--pids-limit=96")] = (
        "--pids-limit=" + str(COMBINED_PIDS_LIMIT))
    args.insert(args.index("--pids-limit=" + str(COMBINED_PIDS_LIMIT)) + 1,
                "--cpus=" + str(COMBINED_CPU_LIMIT))
    args[args.index("--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup")] = (
        "--tmpfs=/tmp:rw,nosuid,nodev,size=768m,notmpcopyup")
    start = args.index(image_id)
    args[start + 1:] = ["/app/codingassistant-coding-runner.mjs"]
    return args

def parse_cgroup_pids(raw: str) -> dict[str, int]:
    """Never accept nonnumeric cgroup information into evidence."""
    if not 0 < len(raw) <= 160:
        raise ValueError("Invalid bounded pids result")
    item = json.loads(raw)
    if (type(item) is not dict or
            set(item) != {"limit", "peak", "max_events"} or
            any(type(value) is not int or not 0 <= value <= 10**9
                for value in item.values()) or
            item["limit"] != COMBINED_PIDS_LIMIT or
            item["peak"] > item["limit"] or
            item["max_events"] > 10**6):
        raise ValueError("Unexpected guest cgroup accounting")
    return item


def query_cgroup_pids(name: str) -> dict[str, int] | None:
    """Read kernel cgroup numeric counters only, before disposable cleanup."""
    if re.fullmatch(r"ca-m17-acpx-[0-9a-f]{12}", name) is None:
        return None
    try:
        result = fixed([
            "podman", "exec", "--interactive=false", name,
            "node", "-e", CGROUP_PIDS_JS,
        ], timeout=15)
        return parse_cgroup_pids(result.stdout) if result.returncode == 0 else None
    except (ValueError, TypeError, OSError, subprocess.TimeoutExpired):
        return None


def gateway_rpc_command(name: str, plan_hash: str,
                        method: str = METHOD) -> list[str]:
    if (not re.fullmatch(r"ca-m17-acpx-[0-9a-f]{12}", name) or
            HEX64.fullmatch(plan_hash) is None or
            method not in (METHOD, STATUS_METHOD)):
        raise ValueError("Invalid isolated Gateway request")
    params = json.dumps({"planHash": plan_hash}, separators=(",", ":"))
    return ["podman", "exec", "--interactive=false", name, "node",
            "/app/openclaw.mjs", "gateway", "call", method,
            "--params", params, "--json", "--timeout",
            "900000" if method == METHOD else "30000"]

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

def parse_gateway_status(raw: str) -> dict[str, object]:
    """Strict, no-free-text status contract; never print or store raw RPC output."""
    if not 0 < len(raw) <= 1500:
        raise ValueError("Missing/oversized bounded diagnostic")
    value = json.loads(raw)
    if (type(value) is not dict or
            set(value) != {"phase", "failureAt", "claimed", "turnSucceeded",
                           "lastFailure"} or
            value.get("phase") not in SAFE_GATEWAY_PHASES or
            value.get("failureAt") not in SAFE_GATEWAY_PHASES | {None} or
            type(value.get("claimed")) is not bool or
            type(value.get("turnSucceeded")) is not bool):
        raise ValueError("Unexpected bounded diagnostic response")
    failure = value["lastFailure"]
    if failure is not None:
        if (type(failure) is not dict or
                set(failure) != {"kind", "errorCode", "modelReason",
                                 "exitCode", "signal", "systemCode"} or
                failure.get("kind") not in SAFE_FAILURE_KINDS or
                failure.get("errorCode") not in SAFE_FAILURE_CODES or
                failure.get("modelReason") not in SAFE_MODEL_REASONS | {None} or
                failure.get("signal") not in SAFE_EXIT_SIGNALS | {None} or
                failure.get("systemCode") not in SAFE_SYSTEM_CODES | {None} or
                (failure.get("exitCode") is not None and
                 (type(failure["exitCode"]) is not int or
                  not 0 <= failure["exitCode"] <= 255))):
            raise ValueError("Unrecognized ACP failure category")
    return value


def query_gateway_status(name: str, plan_hash: str) -> dict[str, object] | None:
    """On RPC failure, ask the SAME pinned plugin for four bounded booleans/stages."""
    try:
        result = fixed(
            gateway_rpc_command(name, plan_hash, STATUS_METHOD), timeout=40)
        if result.returncode != 0:
            return None
        return parse_gateway_status(result.stdout)
    except (OSError, ValueError, TypeError, subprocess.TimeoutExpired):
        return None


def trace_stage(stage: str) -> None:
    """Fixed-category progress only; never print an agent message or source."""
    if stage not in {
        "ScopeVerified", "WorkspacePrepared", "InferenceBridgeReady",
        "GatewayLaunched", "GatewayReady", "CodingRequestSent",
        "InferenceRequestVerified",
        "CodingRequestReturned", "GatewayRemoved", "EvaluatorStarted",
        "EvaluatorPassed",
    }:
        raise ValueError("Unknown progress stage")
    print("CA_M1_STAGE:" + stage, flush=True)


def run_gateway(image: str, worktree: pathlib.Path, socket_dir: pathlib.Path,
                plan_hash: str, evidence: dict[str, object] | None = None, *,
                journal=None) -> dict[str, object]:
    name = journal.container_name() if journal else (
        "ca-m17-acpx-" + secrets.token_hex(6))
    status = evidence if evidence is not None else {}
    status.update({
        "phase": "gateway_start", "health": None, "rpc": None,
        "plugin": None, "removed": False, "container_started": False,
        "rpc_nonzero": False, "cgroup_pids": None,
    })
    with tempfile.TemporaryDirectory(prefix="ca-m112-credentials-") as temporary:
        env = pathlib.Path(temporary) / "gateway.env"
        descriptor = os.open(env, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8") as out:
            out.write("OPENCLAW_GATEWAY_TOKEN=" + secrets.token_hex(32) + "\n")
            out.write("CA_M112_PLAN_HASH=" + plan_hash + "\n")
            out.write("OPENCLAW_SKIP_CHANNELS=1\n")
            out.write("OPENCLAW_SKIP_CANVAS_HOST=1\n")
        try:
            # Crash consistency: register the one managed guest BEFORE it can
            # exist in Podman. A future reconciler will not guess by name.
            if journal:
                journal.advance("RUNNING", container_name=name)
            result = fixed(container_command(
                image, name, env, worktree, socket_dir,
                **({"managed_run_id": journal.run_id} if journal else {})),
                timeout=45)
            if result.returncode != 0:
                raise SafeGateBlocked("GATEWAY_LAUNCH_FAILED")
            status["container_started"] = True
            trace_stage("GatewayLaunched")
            status["phase"] = "gateway_readiness"
            for _ in range(50):
                alive = fixed(["podman", "inspect", "--format",
                               "{{.State.Running}}", name], timeout=12)
                if alive.returncode != 0 or alive.stdout.strip() != "true":
                    raise SafeGateBlocked("GATEWAY_EXITED_BEFORE_READY")
                probe = fixed(["podman", "exec", "--interactive=false",
                               name, "node", "-e", NODE_HTTP], timeout=25)
                if probe.returncode != 0:
                    raise SafeGateBlocked("GATEWAY_HEALTH_PROBE_FAILED")
                codes = parse_codes(probe.stdout)
                status["health"] = codes
                if all(codes[k] == 200 for k in ("/healthz", "/startupz", "/readyz")):
                    break
                time.sleep(2)
            else:
                raise SafeGateBlocked("GATEWAY_READINESS_TIMEOUT")
            trace_stage("GatewayReady")
            status["phase"] = "coding_rpc"
            trace_stage("CodingRequestSent")
            call = fixed(gateway_rpc_command(name, plan_hash), timeout=950)
            if call.returncode != 0:
                status["rpc_nonzero"] = True
                status["plugin"] = query_gateway_status(name, plan_hash)
                raise SafeGateBlocked("CODING_RPC_NONZERO")
            status["phase"] = "coding_rpc_validation"
            try:
                status["rpc"] = parse_rpc(call.stdout)
            except (ValueError, TypeError):
                status["plugin"] = query_gateway_status(name, plan_hash)
                raise SafeGateBlocked("CODING_RPC_INVALID_RESULT")
            status["phase"] = "coding_rpc_complete"
            trace_stage("CodingRequestReturned")
        finally:
            # An observed pids.events.max > 0 proves that the cgroup
            # reached its task limit. A zero counter does not prove an
            # unrelated Bun abort cause. Never retain raw logs.
            status["cgroup_pids"] = query_cgroup_pids(name)
            try:
                fixed(["podman", "rm", "-f", name], timeout=30)
                check = fixed(["podman", "container", "exists", name], timeout=15)
                status["removed"] = check.returncode == 1
            except (OSError, subprocess.TimeoutExpired):
                status["removed"] = False
            if not status["removed"]:
                status["phase"] = "cleanup_failed"
                raise SafeGateBlocked("GATEWAY_CLEANUP_FAILED")
            trace_stage("GatewayRemoved")
    return status

def request_exact_approval(plan_hash: str) -> bool:
    """Show a complete pasteable authorization, never accept a bare APPROVE."""
    if not HEX64.fullmatch(plan_hash):
        raise ValueError("Invalid plan fingerprint")
    approval = "APPROVE " + plan_hash
    print("\nTo authorize only this exact disposable coding plan,")
    print("copy and paste the FULL line below (including the hash):")
    print()
    print("  " + approval)
    print()
    print("Press Enter without typing anything to decline safely.")
    try:
        response = input("Approval (full line): ")
    except EOFError:
        return False
    return response.strip() == approval


def preflight(prepare: bool = False,
              control: dict[str, object] | None = None
              ) -> tuple[dict[str, object], str, str, str, str, str]:
    """Model-free exact executable preview, same verified inputs used at execution.

    --prepare-only may build the pinned offline derivative, but never launches
    an agent. A preview alone never creates a ledger row or grants consent.
    """
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
    source_hash = fingerprint()
    # Reuse only an *independently re-inspected* image whose immutable source
    # fingerprint and base provenance match. Avoid rebuilding on each restart.
    # A stale tag after reviewed source changes is replaced offline, then
    # rechecked. Unexpected provenance blocks non-preparation execution.
    try:
        image = inspect_image(source_hash)
    except ValueError:
        if not prepare:
            raise
        image = None
    if prepare and image is None:
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
        raise ValueError("Derived image absent; opt in with --prepare-only")
    plan, plan_hash = build_approved_plan(
        base_hash, image, digest, manifest_hash, eval_img["image_id"],
        control=control)
    return plan, plan_hash, image, digest, eval_img["image_id"], base_hash


def control_binding(args: argparse.Namespace) -> dict[str, object] | None:
    fields = (args.control_task_id, args.control_run_id,
              args.control_plan_version, args.control_plan_hash)
    if all(value is None for value in fields):
        return None
    if any(value is None for value in fields):
        raise ValueError("Incomplete M2 control-plane binding")
    binding = {"task_id": args.control_task_id, "run_id": args.control_run_id,
               "plan_version": args.control_plan_version,
               "plan_hash": args.control_plan_hash}
    # This same validator is used before every executable operation.
    build_approved_plan("0" * 64, "1" * 64, "2" * 64,
                        "3" * 64, "4" * 64, binding)
    return binding


def execute(prepare: bool, journal=None, *,
            control: dict[str, object] | None = None,
            approval_from_stdin: str | None = None) -> dict[str, object]:
    report: dict[str, object] = {
        "schema_version": 1, "stage": "M1.12-approved-gateway-coding",
        "utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "BLOCKED", "reason_code": None, "phase": "preflight",
        "approved": False,
        "plan_hash": None, "base_image": OWNER_IMAGE_ID,
        "derived_image": None, "model": MODEL, "model_digest": None,
        "gateway": None, "broker": None, "source_before": None,
        "source_after": None, "evaluator": None, "fixture_unchanged": None,
        "sentinel_unchanged": None, "inference_separate_from_evaluator": True,
        "original_repo_mounted": False, "git_remote_enabled": False,
        "external_network_enabled": False,
    }
    try:
        if journal:
            journal.advance("PREFLIGHT")
        plan, plan_hash, image, digest, evaluator_id, base_hash = preflight(
            prepare, control=control)
        report["model_digest"] = digest
        report["derived_image"] = image
        report["plan_hash"] = plan_hash
        trace_stage("ScopeVerified")
        print("Approved-scope preview:", plan["assignment"])
        print("  fixture:", TASK, "model:", MODEL)
        print("  disposable writable file:", "api/Program.cs")
        print("  exact plan SHA256:", plan_hash)
        if journal:
            journal.advance("AWAITING_APPROVAL", plan_hash=plan_hash)
        if approval_from_stdin is None:
            approved = request_exact_approval(plan_hash)
        else:
            approved = hmac.compare_digest(
                approval_from_stdin, "APPROVE " + plan_hash)
        if not approved:
            report["status"] = "NOT_APPROVED"
            if journal:
                journal.advance("DECLINED", result_code="NOT_APPROVED")
            print("Approval declined or incomplete; no coding session was started.")
            return report
        report["approved"] = True
        if journal:
            journal.advance("APPROVED")
        report["phase"] = "disposable_workspace"

        with tempfile.TemporaryDirectory(prefix="ca-m112-isolated-") as temporary:
            root = pathlib.Path(temporary)
            project = root / "approved"
            shutil.copytree(FIXTURES / TASK, project)
            root.chmod(0o700)
            sibling = root / "unmounted.txt"
            sibling.write_text("UNMOUNTED-DO-NOT-TOUCH", encoding="utf-8")
            report["source_before"] = scan_workspace(project, TASK)
            trace_stage("WorkspacePrepared")
            ipc = root / "ipc"
            ipc.mkdir(mode=0o700)
            broker_state = {"attempts": 0, "verified": 0, "failed": 0, "denied": 0}
            # Shared dictionaries retain safe partial evidence even if an
            # isolated Gateway or its ACP session fails before returning.
            report["broker"] = broker_state
            gateway_evidence: dict[str, object] = {}
            report["gateway"] = gateway_evidence
            report["phase"] = "gateway_and_inference"
            with serve_bridge(ipc / "ollama.sock",
                    broker_for(MODEL, digest, broker_state)):
                trace_stage("InferenceBridgeReady")
                try:
                    gateway = run_gateway(
                        image, project, ipc, plan_hash, gateway_evidence,
                        **({"journal": journal} if journal else {}))
                finally:
                    # The disposable project survives the Gateway removal but
                    # is never uploaded to GitHub or printed with its content.
                    report["phase"] = "gateway_complete_or_blocked"
                    try:
                        report["source_after"] = scan_workspace(project, TASK)
                    except (OSError, ValueError):
                        report["source_after"] = None
                        gateway_evidence["post_run_workspace_integrity"] = False
            if report["source_after"] is None:
                raise SafeGateBlocked("WORKSPACE_INTEGRITY_FAILED")
            report["phase"] = "source_integrity"
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
            report["phase"] = "offline_acceptance"
            trace_stage("EvaluatorStarted")
            if journal:
                journal.advance("VERIFYING")
            eval_dir = root / "offline-evaluation"
            result = run_evaluator(
                evaluator_id, TASK, project, sibling,
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
            trace_stage("EvaluatorPassed")
            # Only the two verified fixed-fixture paths below are read, and
            # only this approved C# source may be displayed. Workspace is
            # disposable and deleted after report persistence.
            original_text = (FIXTURES / TASK / "api" / "Program.cs").read_text(
                encoding="utf-8")
            repaired_text = (project / "api" / "Program.cs").read_text(
                encoding="utf-8")
            diff = "".join(difflib.unified_diff(
                original_text.splitlines(keepends=True),
                repaired_text.splitlines(keepends=True),
                fromfile="a/api/Program.cs", tofile="b/api/Program.cs",
                n=3))
            report["approved_patch"] = (
                diff if len(diff.encode("utf-8")) <= 7000 else "")
            report["patch_omitted"] = len(diff.encode("utf-8")) > 7000
            report["status"] = "GATEWAY_APPROVED_CODING_PASS"
            report["phase"] = "complete"
    except (OSError, RuntimeError, ValueError, TypeError, KeyError,
            AssertionError, subprocess.TimeoutExpired,
            http.client.HTTPException) as exc:
        report["reason_code"] = (
            exc.category if isinstance(exc, SafeGateBlocked)
            else type(exc).__name__)
    return report

def bounded_result(report: dict, request_id: str, plan_hash: str) -> dict:
    """Public-to-local-owner report: selected fixed metadata only.

    Never return private ledger paths, raw ACP/Gateway/model logs, unapproved
    code, host source, environment, or exception messages.
    """
    if not re.fullmatch(r"[0-9a-f]{32}", request_id) or not HEX64.fullmatch(plan_hash):
        raise ValueError("Invalid bound result ID")
    if type(report) is not dict or report.get("plan_hash") != plan_hash:
        raise ValueError("Stored result doesn't match immutable plan")
    status = report.get("status")
    if status not in ("GATEWAY_APPROVED_CODING_PASS", "BLOCKED", "NOT_APPROVED"):
        raise ValueError("Invalid final result status")
    phase = report.get("phase", "")
    reason = report.get("reason_code")
    if (type(phase) is not str or not re.fullmatch(r"[a-z_]{1,45}", phase) or
            reason is not None and (type(reason) is not str or
            not re.fullmatch(r"[A-Za-z_]{1,64}", reason))):
        raise ValueError("Invalid safe result category")
    evaluator = report.get("evaluator")
    verified = (evaluator.get("verified") if type(evaluator) is dict else None)
    if verified is not None and type(verified) is not bool:
        raise ValueError("Invalid evaluator verdict")
    before = report.get("source_before")
    after = report.get("source_after")
    changed = (before.get("api/Program.cs") != after.get("api/Program.cs")
               if type(before) is dict and type(after) is dict else None)
    broker = report.get("broker")
    calls = (broker.get("verified") if type(broker) is dict else None)
    if calls is not None and (type(calls) is not int or not 0 <= calls <= MAX_REQUESTS):
        raise ValueError("Invalid inference request count")
    patch = report.get("approved_patch", "")
    omitted = report.get("patch_omitted", False)
    if (type(patch) is not str or len(patch.encode("utf-8")) > 7000 or
            (patch and not patch.startswith("--- a/api/Program.cs\n")) or
            type(omitted) is not bool):
        raise ValueError("Invalid fixed-fixture patch")
    if status != "GATEWAY_APPROVED_CODING_PASS" or verified is not True:
        patch = ""  # Never present unverified code as approved output.
        omitted = False
    return {
        "schema": "codingassistant-run-result-v1",
        "available": True,
        "runId": request_id,
        "planHash": plan_hash,
        "status": status,
        "phase": phase,
        "reasonCode": reason,
        "evaluatorVerified": verified,
        "sourceChanged": changed,
        "inferenceRequests": calls,
        "patch": patch,
        "patchOmitted": omitted,
    }


def read_result_by_request(ledger: RunLedger, request_id: str) -> dict:
    """Pure read; never starts a model, replays work or reconciles the guest."""
    if not re.fullmatch(r"[0-9a-f]{32}", request_id):
        raise ValueError("Invalid exact request ID")
    row = ledger.get_by_request(request_id)
    if row is None or not row["evidence_name"]:
        return {"schema": "codingassistant-run-result-v1", "available": False}
    if row["state"] not in ("SUCCEEDED", "FAILED", "DECLINED"):
        return {"schema": "codingassistant-run-result-v1", "available": False}
    filename = row["evidence_name"]
    if type(filename) is not str or not re.fullmatch(
            r"m112-approved-coding-[0-9a-f]{12}\.json", filename):
        raise ValueError("Untrusted evidence name")
    evidence = ARTIFACTS / filename
    fd = os.open(evidence, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0))
    try:
        identity = os.fstat(fd)
        if (not stat.S_ISREG(identity.st_mode) or
                identity.st_uid != os.geteuid() or
                identity.st_mode & 0o077 or identity.st_size > 64_000):
            raise ValueError("Unsafe evidence permissions or size")
        with os.fdopen(fd, "r", encoding="utf-8") as stream:
            fd = -1
            body = stream.read(64_001)
    finally:
        if fd >= 0:
            os.close(fd)
    if len(body) > 64_000:
        raise ValueError("Oversized saved evidence")
    return bounded_result(json.loads(body), request_id, row["plan_hash"])


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare", action="store_true",
                        help="Explicitly build offline image before exact owner approval")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--history", action="store_true",
                      help="Read host-owned durable run history (no model)")
    mode.add_argument("--status", metavar="RUN_ID",
                      help="Read one durable run and its transitions (no model)")
    mode.add_argument("--recover", metavar="RUN_ID",
                      help="Safely reconcile one interrupted run (never replays model)")
    mode.add_argument("--preview-json", action="store_true",
                      help="Return bound M1 executable plan without model or approval")
    mode.add_argument("--prepare-only", action="store_true",
                      help="Build pinned derivative offline and preview, never execute")
    mode.add_argument("--recover-request-id", metavar="32_HEX",
                      help="Reconcile by exact M2 idempotency key, never replay")
    mode.add_argument("--result-by-request-id", metavar="32_HEX",
                      help="Read sanitized durable fixed-fixture result, no model")
    parser.add_argument("--approval-stdin", action="store_true",
                        help="Consume exact owner authorization from one stdin line")
    parser.add_argument("--control-task-id")
    parser.add_argument("--control-run-id")
    parser.add_argument("--control-plan-version", type=int)
    parser.add_argument("--control-plan-hash")
    parser.add_argument("--request-id", metavar="32_HEX",
                        help="Caller idempotency key; duplicate key NEVER re-executes")
    args = parser.parse_args()
    inspect_mode = (args.history or args.status or args.recover or
                    args.recover_request_id or args.result_by_request_id or
                    args.preview_json or args.prepare_only)
    if args.prepare and inspect_mode:
        parser.error("--prepare applies only to a new interactive coding run")
    if args.request_id is not None and inspect_mode:
        parser.error("--request-id applies only to a new approved run")
    if args.approval_stdin and inspect_mode:
        parser.error("--approval-stdin applies only to a coding run")
    try:
        control = control_binding(args)
    except ValueError as exc:
        parser.error(str(exc))
    if args.preview_json or args.prepare_only:
        try:
            plan, plan_hash, *_ = preflight(
                prepare=args.prepare_only, control=control)
            print(json.dumps({"schema": "codingassistant-execution-preview-v1",
                              "plan": plan, "plan_hash": plan_hash},
                             sort_keys=True, separators=(",", ":")))
            return 0
        except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired):
            print("M1 executable plan preflight BLOCKED", file=sys.stderr)
            return 2
    approval_text = None
    if args.approval_stdin:
        # This pipe is written only after a durable API authorization. No
        # approval string is ever supplied through argv, env or audit logs.
        approval_text = sys.stdin.readline(160).rstrip("\r\n")
    if args.recover_request_id and control is not None:
        parser.error("Recovery requires request ID only, not a control binding")
    # This directory is private (0700) regardless of artifacts/m1's
    # historical permissions. No public paths or logs enter SQLite.
    with RunLedger(LEDGER_DIR) as ledger:
        if args.result_by_request_id:
            try:
                print(json.dumps(read_result_by_request(
                    ledger, args.result_by_request_id), separators=(",", ":")))
                return 0
            except (OSError, ValueError, TypeError, json.JSONDecodeError):
                print("M1 result evidence unavailable", file=sys.stderr)
                return 2
        if args.history:
            print(json.dumps(ledger.history(), indent=2))
            return 0
        if args.status:
            print(json.dumps({
                "run": ledger.get(args.status),
                "transitions": ledger.transitions(args.status),
            }, indent=2))
            return 0
        if args.recover_request_id:
            if sys.platform != "linux" or os.geteuid() == 0:
                print("Recovery blocked: Linux rootless operation required")
                return 2
            prior = ledger.get_by_request(args.recover_request_id)
            if prior is None:
                print('{"result":"NOT_FOUND"}')
                return 0
            outcome = ledger.reconcile(prior["run_id"])
            print(json.dumps({"result": outcome["result"]}, sort_keys=True))
            return 0 if outcome["result"] in (
                "INTERRUPTED_CLEAN", "ALREADY_TERMINAL") else 2
        if args.recover:
            if sys.platform != "linux" or os.geteuid() == 0:
                print("Recovery blocked: Linux rootless operation required")
                return 2
            result = ledger.reconcile(args.recover)
            print(json.dumps(result, indent=2))
            return 0 if result["result"] in (
                "INTERRUPTED_CLEAN", "ALREADY_TERMINAL", "ACTIVE") else 2
        try:
            journal = ledger.start(request_id=args.request_id)
        except DuplicateRequest as prior:
            print("IDEMPOTENCY_KEY_ALREADY_USED — no model or coding action attempted")
            print(json.dumps(prior.record, indent=2))
            return 3
        try:
            print("Durable run ID:", journal.run_id)
            report = execute(prepare=args.prepare, journal=journal,
                             control=control,
                             approval_from_stdin=approval_text)
            ARTIFACTS.mkdir(parents=True, exist_ok=True)
            name = "m112-approved-coding-" + uuid.uuid4().hex[:12] + ".json"
            path = ARTIFACTS / name
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8") as stream:
                json.dump(report, stream, indent=2, sort_keys=True)
                stream.write("\n")
                stream.flush()
                os.fsync(stream.fileno())
            # A verified final DB state requires an on-disk evidence artifact.
            final = journal.finish(report, name)
            print("M1.12", report["status"])
            print("  durable run:", journal.run_id, "state:", final)
            print("  approved:", report["approved"], "plan hash:", report["plan_hash"])
            print("  failed/succeeded phase:", report["phase"])
            print("  isolated Gateway / host broker:", report["gateway"], report["broker"])
            print("  independent evaluator:", report["evaluator"])
            if report["reason_code"]:
                print("  stopped safely:", report["reason_code"])
            print("Evidence:", path)
            print("Durable history: python3 tools/m1/approved_gateway_coding.py --status", journal.run_id)
            return 0 if final == "SUCCEEDED" else 2
        except (OSError, ValueError, RuntimeError, sqlite3.Error) as exc:
            # Fail closed. The exclusive lifetime lock is released by finally;
            # a non-terminal row can only be reconciled, NEVER auto-resumed.
            print("M1 durable execution BLOCKED:", type(exc).__name__)
            print("Recover safely: python3 tools/m1/approved_gateway_coding.py --recover", journal.run_id)
            return 2
        finally:
            journal.close()


if __name__ == "__main__":
    raise SystemExit(main())
