#!/usr/bin/env python3
"""M1.11: Gateway-owned real ACP doctor, inside pinned disposable Podman.

After verifying owner M1.7 image and M1.9 OS negative boundaries are
available, start an offline Gateway and send EXACTLY "/acp doctor"
through the Gateway-backed 'agent' CLI (never '--local'). The pinned
OpenClaw v2026.11.5 command calls its registered ACPX runtime.doctor();
the runtime performs ACP initialize with OpenCode and closes it.
NO coding session/new or session/prompt, model provider, host worktree,
network access, repo mutation, or external mount is authorized.

Agent CLI may create disposable Gateway bookkeeping in its own tmpfs.
A generic agent/model response or failure to verify doctor evidence
is BLOCKED. Only safe booleans/protocol metadata are recorded.
"""
from __future__ import annotations

import datetime as dt
import json
import os
import pathlib
import re
import secrets
import subprocess
import sys
import tempfile
import time
import uuid

from acpx_initialize_probe import OWNER_IMAGE_ID
from acpx_opencode_image_probe import (
    ephemeral_gateway, inspect_composed, verify_combined, verify_inputs,
)
from gateway_image_probe import fixed
from gateway_loopback_probe import NODE_HTTP, parse_codes

ROOT = pathlib.Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts/m1"
NAME_RE = re.compile(r"ca-m17-acpx-[0-9a-f]{12}\Z")
SESSION_RE = re.compile(r"agent:main:ca-m111-doctor-[0-9a-f]{12}\Z")
EXPECTED_COMMAND = "/acp doctor"
EXPECTED_DOCTOR = "runtimeDoctor: ok (embedded ACP runtime ready)"
PROTOCOL = re.compile(r"runtimeDoctorDetail:\s*protocolVersion=([A-Za-z0-9._-]{1,32})")
PORT = 18789


def doctor_command(name: str, session_key: str) -> list[str]:
    if not NAME_RE.fullmatch(name) or not SESSION_RE.fullmatch(session_key):
        raise ValueError("Unapproved disposable Gateway or doctor session")
    # A pin-tested Gateway-backed CLI only. No --local, --deliver, --model,
    # custom prompt, input file, arbitrary gateway method or agent exec.
    return [
        "podman", "exec", "--interactive=false", name,
        "node", "/app/openclaw.mjs", "agent",
        "--session-key", session_key,
        "--message", EXPECTED_COMMAND, "--timeout", "45", "--json",
    ]


def parse_doctor(raw: str) -> dict[str, object]:
    if not (0 < len(raw) < 40000):
        raise ValueError("Missing/oversized Gateway doctor CLI response")
    envelope = json.loads(raw)
    if type(envelope) is not dict:
        raise ValueError("Gateway doctor response is not a JSON envelope")
    if envelope.get("status") not in ("ok", "completed", "success", None):
        raise ValueError("Gateway doctor turn did not complete")
    if envelope.get("ok") is False or envelope.get("error"):
        raise ValueError("Gateway doctor returned an error")

    strings: list[str] = []
    # Inspect only standard response fields; no arbitrary traversal of
    # tool payloads, usage traces, agent metadata, or untrusted files.
    def gather(value: object, depth: int) -> None:
        if depth > 5 or len(strings) >= 24:
            return
        if type(value) is str:
            if len(value) <= 20000:
                strings.append(value)
            return
        if type(value) is list:
            for item in value[:12]:
                gather(item, depth + 1)
        elif type(value) is dict:
            for key in ("result", "reply", "payloads", "final", "text", "content"):
                if key in value:
                    gather(value[key], depth + 1)
    gather(envelope, 0)
    matching = [
        text for text in strings
        if ("ACP doctor:" in text and
            "registeredBackend: acpx" in text and
            EXPECTED_DOCTOR in text and
            "activeRuntimeSessions: 0" in text and
            "activeTurns: 0" in text)
    ]
    if len(matching) != 1:
        raise ValueError(
            "Exact registered-backend ACP doctor response not observed; "
            "no model output accepted as proof"
        )
    result = matching[0]
    versions = PROTOCOL.findall(result)
    if len(versions) != 1 or versions[0] != "1":
        raise ValueError("Gateway ACP doctor did not report pinned protocol 1")
    if re.search(r"runtimeDoctor: error|healthy: no", result):
        raise ValueError("ACP backend doctor reported unhealthy")
    return {
        "gateway_command": EXPECTED_COMMAND,
        "backend": "acpx",
        "runtime_doctor": "ready",
        "protocol_version": versions[0],
        "active_acp_runtime_sessions": 0,
        "active_acp_turns": 0,
    }


def classify_doctor_exit(response: subprocess.CompletedProcess[str]) -> dict[str, object]:
    """Summarize a failed CLI request without recording any raw output.

    Strictly bounded, fixed-label classification only. Error text may include
    device IDs, gateway tokens, config values, paths, or provider secrets.
    Neither a substring nor a JSON field value is ever emitted verbatim.
    All unknown errors remain BLOCKED and are not evidence of ACP success.
    """
    if type(response.returncode) is not int or response.returncode == 0:
        raise ValueError("Expected nonzero Gateway doctor exit")
    out = response.stdout if type(response.stdout) is str else ""
    err = response.stderr if type(response.stderr) is str else ""
    combined = (err[:12000] + "\n" + out[:12000]).lower()
    # Ordered from specific to general so a CLI error containing words like
    # "gateway" or "agent" is not prematurely classified as generic.
    categories = (
        ("cli_usage_or_option", (
            "unknown option", "unknown argument", "unrecognized option",
            "invalid option", "required option", "missing required argument",
            "unknown command", "too many arguments")),
        ("device_pairing_required", (
            "pairing required", "device pairing", "device not paired",
            "device approval required")),
        ("authentication_or_scope", (
            "unauthorized", "forbidden", "invalid token", "authentication failed",
            "missing scope", "permission denied", "not authorized",
            "authentication required", "device token")),
        ("gateway_connection", (
            "econnrefused", "connection refused", "gateway closed", "gateway unavailable",
            "websocket closed", "websocket connection", "handshake failed")),
        ("agent_or_session_config", (
            "unknown agent", "agent not found", "session key", "no agent",
            "unconfigured", "agent config", "workspace not configured")),
        ("provider_or_model_config", (
            "api key missing", "no api key", "missing api key",
            "model not found", "no model configured", "provider not configured",
            "auth profile", "model provider")),
        ("acp_backend", (
            "acp runtime failed", "acpx runtime failed", "acp backend",
            "acp doctor failed", "protocol version mismatch")),
        ("request_timeout", (
            "timed out", "timeout exceeded", "deadline exceeded")),
        ("container_or_process", (
            "container state improper", "container is not running",
            "executable file not found", "no such file or directory")),
        ("gateway_request_rejected", (
            "gateway request failed", "request rejected", "invalid request",
            "internal error", "not implemented")),
    )
    category = next(
        (label for label, needles in categories if any(n in combined for n in needles)),
        "unclassified",
    )
    shape = "non_json"
    try:
        payload = json.loads(out[:40000]) if len(out) <= 40000 else None
        if type(payload) is dict:
            shape = "json_object"
        elif type(payload) is list:
            shape = "json_array"
        elif payload is not None:
            shape = "json_scalar"
    except (TypeError, ValueError, json.JSONDecodeError):
        pass
    return {
        "exit_code": response.returncode if -128 <= response.returncode <= 255 else None,
        "category": category,
        "stdout_shape": shape,
        "stdout_present": bool(out.strip()),
        "stderr_present": bool(err.strip()),
        "doctor_header_seen": "ACP doctor:" in out,
        "doctor_ok_marker_seen": EXPECTED_DOCTOR in out,
        "raw_output_retained": False,
    }


def run() -> dict[str, object]:
    report: dict[str, object] = {
        "schema_version": 1, "stage": "M1.11-gateway-owned-acp-doctor",
        "recorded_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "BLOCKED", "image_id": None, "package_preflight": False,
        "gateway_started": False, "gateway_health": None,
        "authenticated_doctor": None, "doctor_failure_diagnostic": None,
        "gateway_removed": False,
        "network_disabled": True, "host_mounts": False,
        "published_ports": False, "acp_coding_session_created": False,
        "coding_prompt_sent": False, "source_modified": False,
        "model_service_access": False, "reason": None,
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        report["reason"] = "Linux rootless non-root Podman required"
        return report
    name = "ca-m17-acpx-" + secrets.token_hex(6)
    session_key = "agent:main:ca-m111-doctor-" + secrets.token_hex(6)
    try:
        m0_image = verify_inputs()
        image_id = inspect_composed(m0_image)
        if image_id != OWNER_IMAGE_ID:
            raise ValueError("M1.7 owner's immutable composed image identity changed")
        report["image_id"] = image_id
        installed = verify_combined(image_id)
        if installed["registry"] != {
            "id": "opencode", "installation": "installed",
            "expectedCommand": True,
        }:
            raise ValueError("OpenCode ACP adapter no longer installed")
        report["package_preflight"] = True
        with tempfile.TemporaryDirectory(prefix="ca-m111-gateway-") as directory:
            envfile = pathlib.Path(directory) / "gateway.env"
            fd = os.open(envfile, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                f.write("OPENCLAW_GATEWAY_TOKEN=" + secrets.token_hex(32) + "\n")
                f.write("OPENCLAW_SKIP_CHANNELS=1\n")
                f.write("OPENCLAW_SKIP_CANVAS_HOST=1\n")
            started = fixed(ephemeral_gateway(image_id, name, envfile),
                            timeout=40)
            if started.returncode != 0:
                raise ValueError("Isolated Gateway failed to start")
            report["gateway_started"] = True
            ready = False
            for _ in range(30):
                alive = fixed(["podman", "inspect", "--format",
                               "{{.State.Running}}", name], timeout=12)
                if alive.returncode != 0 or alive.stdout.strip() != "true":
                    raise ValueError("Gateway exited during doctor readiness")
                health = fixed([
                    "podman", "exec", "--interactive=false", name,
                    "node", "-e", NODE_HTTP,
                ], timeout=20)
                if health.returncode != 0:
                    raise ValueError("Guest loopback health probe failed")
                codes = parse_codes(health.stdout)
                report["gateway_health"] = codes
                if all(codes[key] == 200 for key in (
                        "/healthz", "/startupz", "/readyz")):
                    ready = True
                    break
                time.sleep(2)
            if not ready:
                raise ValueError("Gateway never reached full readiness")

            response = fixed(doctor_command(name, session_key), timeout=90)
            if response.returncode != 0:
                diagnostic = classify_doctor_exit(response)
                report["doctor_failure_diagnostic"] = diagnostic
                raise ValueError(
                    "Gateway ACP doctor command returned nonzero; safe category: " +
                    str(diagnostic["category"]) + "; no raw output retained"
                )
            report["authenticated_doctor"] = parse_doctor(response.stdout)
            report["status"] = "GATEWAY_OWNED_ACP_DOCTOR_PASS"
    except (ValueError, OSError, KeyError, TypeError, json.JSONDecodeError,
            subprocess.TimeoutExpired) as exc:
        report["reason"] = str(exc)[:240]
    finally:
        # No other service may be touched, even on preflight or timeout.
        try:
            fixed(["podman", "rm", "-f", name], timeout=25)
            exists = fixed(["podman", "container", "exists", name], timeout=15)
            report["gateway_removed"] = exists.returncode == 1
            if not report["gateway_removed"]:
                report["status"] = "BLOCKED"
                report["reason"] = "Disposable Gateway cleanup not verified"
        except (OSError, subprocess.TimeoutExpired):
            report["status"] = "BLOCKED"
            report["gateway_removed"] = False
            report["reason"] = "Disposable Gateway cleanup failed"
    if not report["gateway_removed"]:
        report["status"] = "BLOCKED"
    return report


def main() -> int:
    result = run()
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    file = ARTIFACTS / ("gateway-acp-doctor-" + uuid.uuid4().hex[:12] + ".json")
    descriptor = os.open(file, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2, sort_keys=True)
        stream.write("\n")
    print("M1.11", result["status"])
    print("  combined immutable image:", result["image_id"] or "NOT_VERIFIED")
    print("  package preflight:", result["package_preflight"])
    print("  Gateway health:", result["gateway_health"])
    print("  Gateway-owned ACP doctor:", result["authenticated_doctor"])
    if result["doctor_failure_diagnostic"]:
        print("  sanitized CLI failure:", result["doctor_failure_diagnostic"])
    print("  disposable Gateway removed:", result["gateway_removed"])
    print("  ACP coding session / prompt / model: NOT_REQUESTED")
    if result["reason"]:
        print("  blocker:", result["reason"])
    print("Evidence:", file)
    return 0 if result["status"] == "GATEWAY_OWNED_ACP_DOCTOR_PASS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
