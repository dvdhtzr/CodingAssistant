#!/usr/bin/env python3
"""Correct M1.11 Gateway ACP doctor integration without slash-command injection.

Builds an optional, copy-only/no-network local layer on immutable M1.7
OpenClaw+ACPX+OpenCode image. A tiny, explicit diagnostic plugin registers
'codingassistant.acp.doctor' as operator.read in the ACTUAL Gateway, calls
the registered ACPX backend's doctor() and returns bounded status fields.

No ordinary agent CLI turn, model, prompt, ACP coding session, project mount,
remote network, external package install, source workspace, or host Node.
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import pathlib
import re
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import uuid

from acpx_initialize_probe import OWNER_IMAGE_ID
from acpx_opencode_image_probe import (
    inspect_composed, verify_combined, verify_inputs, ephemeral_gateway,
)
from gateway_image_probe import fixed
from gateway_loopback_probe import NODE_HTTP, parse_codes

ROOT = pathlib.Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts/m1"
CONTEXT = ROOT / "infra/m1"
CONTAINERFILE = CONTEXT / "Containerfile.gateway-doctor"
CONFIG_FILE = CONTEXT / "doctor-config.json"
PLUGIN_DIR = CONTEXT / "doctor-plugin"
SOURCES = (
    CONTAINERFILE, CONFIG_FILE, PLUGIN_DIR / "index.mjs",
    PLUGIN_DIR / "openclaw.plugin.json", PLUGIN_DIR / "package.json",
)
TAG = "localhost/codingassistant-m111-gateway-doctor:2026.11.5"
NAME_RE = re.compile(r"ca-m17-acpx-[a-f0-9]{12}\Z")
HEX64 = re.compile(r"(?:sha256:)?([a-f0-9]{64})\Z")
METHOD = "codingassistant.acp.doctor"
EXPECTED = {
    "backend": "acpx", "runtimeDoctorOk": True, "protocolVersion": "1",
    "codingSessionCreated": False, "promptSent": False,
}
LABEL_PREFIX = "io.github.dvdhtzr.codingassistant.m111"
CONFIG_GUEST_PATH = "/app/codingassistant-doctor-config.json"


def source_fingerprint() -> str:
    """Pin both the build instructions and all copied plugin/config sources."""
    digest = hashlib.sha256()
    for path in SOURCES:
        if not path.is_file() or path.is_symlink():
            raise ValueError("M1.11 source file missing or is a symlink")
        data = path.read_bytes()
        if not 0 < len(data) < 120_000:
            raise ValueError("M1.11 source file size outside trust bound")
        digest.update(path.relative_to(ROOT).as_posix().encode("utf-8") + b"\0")
        digest.update(data + b"\0")
    conf = json.loads(CONFIG_FILE.read_text(encoding="utf-8"))
    if (conf.get("acp") != {
            "enabled": True, "backend": "acpx", "allowedAgents": ["opencode"]
            } or
        conf.get("plugins", {}).get("load", {}).get("paths") !=
            ["/app/codingassistant-doctor"]):
        raise ValueError("Unapproved Gateway diagnostic/ACP allowlist configuration")
    acpx = conf["plugins"]["entries"]["acpx"]["config"]
    if (acpx.get("probeAgent") != "opencode" or
            acpx.get("agents") != {
                "opencode": {"command": "/usr/local/bin/opencode", "args": ["acp"]}} or
            acpx.get("permissionMode") != "deny-all" or
            acpx.get("nonInteractivePermissions") != "deny" or
            acpx.get("mcpServers") != {} or
            acpx.get("agentProcessEnvAllow") != []):
        raise ValueError("Pinned OpenCode command/ACP default-deny controls changed")
    plugin = (PLUGIN_DIR / "index.mjs").read_text(encoding="utf-8")
    if (plugin.count('api.registerGatewayMethod(') != 1 or
            '"codingassistant.acp.doctor"' not in plugin or
            '"operator.read"' not in plugin or
            'getAcpRuntimeBackend("acpx")' not in plugin):
        raise ValueError("Unexpected Gateway diagnostic plugin entrypoint")
    if "acp" not in (PLUGIN_DIR / "openclaw.plugin.json").read_text():
        raise ValueError("Unexpected Gateway diagnostic plugin manifest")
    return digest.hexdigest()


def build_command(fingerprint: str) -> list[str]:
    if not HEX64.fullmatch(fingerprint):
        raise ValueError("Source fingerprint must be SHA256")
    return [
        "podman", "build", "--pull=never", "--network=none",
        "--file", str(CONTAINERFILE), "--tag", TAG,
        "--build-arg", "BASE_IMAGE=" + OWNER_IMAGE_ID,
        "--label", LABEL_PREFIX + ".base=" + OWNER_IMAGE_ID,
        "--label", LABEL_PREFIX + ".sources=" + fingerprint,
        "--label", LABEL_PREFIX + ".method=" + METHOD,
        str(CONTEXT),
    ]


def inspect_image(fingerprint: str) -> str | None:
    response = fixed(["podman", "image", "inspect", TAG], timeout=25)
    if response.returncode != 0:
        return None
    decoded = json.loads(response.stdout)
    if type(decoded) is not list or len(decoded) != 1 or type(decoded[0]) is not dict:
        raise ValueError("Unexpected M1.11 image inspect payload")
    meta = decoded[0]
    match = HEX64.fullmatch(meta.get("Id", ""))
    if match is None or match.group(1) == OWNER_IMAGE_ID:
        raise ValueError("Missing/unchanged M1.11 derived immutable image ID")
    labels = meta.get("Labels", {})
    if (type(labels) is not dict or
            labels.get(LABEL_PREFIX + ".base") != OWNER_IMAGE_ID or
            labels.get(LABEL_PREFIX + ".sources") != fingerprint or
            labels.get(LABEL_PREFIX + ".method") != METHOD or
            TAG not in meta.get("RepoTags", [])):
        raise ValueError("M1.11 image provenance mismatch")
    return match.group(1)


def gateway_command(image_id: str, name: str,
                    private_env: pathlib.Path) -> list[str]:
    if not HEX64.fullmatch(image_id) or not NAME_RE.fullmatch(name):
        raise ValueError("Wrong image or disposable Gateway name")
    cmd = ephemeral_gateway(image_id, name, private_env)
    index = cmd.index(image_id)
    # Configuration is an immutable, public file from the offline layer.
    # The random live Gateway token remains in the private 0600 env file.
    cmd[index:index] = [
        "--env=OPENCLAW_CONFIG_PATH=" + CONFIG_GUEST_PATH,
    ]
    return cmd


def rpc_command(name: str) -> list[str]:
    if not NAME_RE.fullmatch(name):
        raise ValueError("Invalid random Gateway container identity")
    return [
        "podman", "exec", "--interactive=false", name,
        "node", "/app/openclaw.mjs", "gateway", "call", METHOD,
        "--params", "{}", "--json", "--timeout", "60000",
    ]


def parse_result(raw: str) -> dict[str, object]:
    if not 0 < len(raw) < 2000:
        raise ValueError("Missing/oversized registered ACP backend RPC result")
    response = json.loads(raw)
    if (type(response) is not dict or set(response) != set(EXPECTED)
            or any(type(response[key]) is not type(value) or response[key] != value
                   for key, value in EXPECTED.items())):
        raise ValueError("Gateway-owned ACP doctor did not satisfy pinned protocol contract")
    return {"backend": "acpx", "protocol_version": "1",
            "actual_backend_doctor": True, "coding_session": False,
            "model_inference": False}


def verify_gateway_rpc(image_id: str) -> dict[str, object]:
    name = "ca-m17-acpx-" + secrets.token_hex(6)
    result: dict[str, object] = {
        "gateway_health": None, "doctor": None, "removed": False,
    }
    try:
        with tempfile.TemporaryDirectory(prefix="ca-m111-rpc-") as temporary:
            envfile = pathlib.Path(temporary) / "gateway.env"
            fd = os.open(envfile, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8") as output:
                output.write("OPENCLAW_GATEWAY_TOKEN=" + secrets.token_hex(32) + "\n")
                output.write("OPENCLAW_SKIP_CHANNELS=1\n")
                output.write("OPENCLAW_SKIP_CANVAS_HOST=1\n")
            started = fixed(gateway_command(image_id, name, envfile), timeout=45)
            if started.returncode != 0:
                raise ValueError("Diagnostic-only Gateway failed to start")
            for attempt in range(40):
                alive = fixed(["podman", "inspect", "--format",
                               "{{.State.Running}}", name], timeout=12)
                if alive.returncode != 0 or alive.stdout.strip() != "true":
                    raise ValueError("Diagnostic Gateway exited unexpectedly")
                response = fixed([
                    "podman", "exec", "--interactive=false", name,
                    "node", "-e", NODE_HTTP,
                ], timeout=20)
                if response.returncode != 0:
                    raise ValueError("Diagnostic Gateway health request failed")
                codes = parse_codes(response.stdout)
                result["gateway_health"] = codes
                if all(codes[k] == 200 for k in (
                        "/healthz", "/startupz", "/readyz")):
                    break
                time.sleep(2)
            else:
                raise ValueError("Diagnostic Gateway never reached full readiness")
            call = fixed(rpc_command(name), timeout=90)
            if call.returncode != 0:
                # Never save token-bearing output or claim protocol success.
                raise ValueError(
                    "Registered Gateway ACP diagnostic RPC returned nonzero "
                    "(raw output discarded)"
                )
            result["doctor"] = parse_result(call.stdout)
    finally:
        try:
            fixed(["podman", "rm", "-f", name], timeout=25)
            exists = fixed(["podman", "container", "exists", name], timeout=15)
            result["removed"] = exists.returncode == 1
        except (OSError, subprocess.TimeoutExpired):
            result["removed"] = False
        if not result["removed"]:
            raise ValueError("Diagnostic Gateway cleanup could not be verified")
    return result


def evaluate(prepare: bool) -> dict[str, object]:
    record: dict[str, object] = {
        "schema_version": 1, "stage": "M1.11-repaired-registered-backend-rpc",
        "recorded_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "BLOCKED", "prepare_requested": prepare,
        "owner_base_image": OWNER_IMAGE_ID, "derived_image_id": None,
        "source_fingerprint": None, "verified_package": False,
        "gateway_evidence": None, "build_log": None,
        "network_disabled": True, "no_host_mounts": True,
        "no_published_ports": True, "no_cli_agent_turn": True,
        "no_coding_session": True, "no_model": True,
        "reason": None,
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        record["reason"] = "Linux rootless Podman required"
        return record
    try:
        original = verify_inputs()
        image = inspect_composed(original)
        if image != OWNER_IMAGE_ID:
            raise ValueError("Owner-qualified M1.7 source image changed")
        tools = verify_combined(image)
        if tools["registry"] != {
                "id": "opencode", "installation": "installed", "expectedCommand": True}:
            raise ValueError("Pinned OpenCode ACP runtime no longer installed")
        record["verified_package"] = True
        fingerprint = source_fingerprint()
        record["source_fingerprint"] = fingerprint
        if prepare:
            if shutil.disk_usage(ROOT).free < 2 * 1024**3:
                raise ValueError("Insufficient free disk space for offline image layer")
            ARTIFACTS.mkdir(parents=True, exist_ok=True)
            build_log = ARTIFACTS / ("gateway-backend-rpc-build-" + uuid.uuid4().hex[:12] + ".log")
            record["build_log"] = str(build_log)
            cmd = build_command(fingerprint)
            with build_log.open("x", encoding="utf-8") as log:
                built = subprocess.run(cmd, stdin=subprocess.DEVNULL,
                                       stdout=log, stderr=subprocess.STDOUT,
                                       timeout=600, check=False)
            if built.returncode != 0:
                raise ValueError("Offline diagnostic-only image build failed")
        derived_id = inspect_image(fingerprint)
        if derived_id is None:
            raise ValueError("Diagnostic-only derived image absent; opt in with --prepare")
        record["derived_image_id"] = derived_id
        record["gateway_evidence"] = verify_gateway_rpc(derived_id)
        if not record["gateway_evidence"]["removed"]:
            raise ValueError("Gateway removal must be confirmed")
        record["status"] = "GATEWAY_OWNED_ACP_DOCTOR_PASS"
    except (ValueError, OSError, TypeError, KeyError, json.JSONDecodeError,
            subprocess.TimeoutExpired) as exc:
        record["reason"] = str(exc)[:220]
    return record


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare", action="store_true",
                        help="Build a diagnostic-only local layer entirely offline")
    args = parser.parse_args()
    report = evaluate(args.prepare)
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    destination = ARTIFACTS / ("gateway-backend-rpc-" + uuid.uuid4().hex[:12] + ".json")
    fd = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2, sort_keys=True)
        stream.write("\n")
    print("M1.11", report["status"])
    print("  verified base image:", report["owner_base_image"])
    print("  derived diagnostic image:", report["derived_image_id"])
    print("  Gateway ACPX protocol:", report["gateway_evidence"])
    print("  agent CLI / coding session / model: NOT_ATTEMPTED")
    if report["build_log"]:
        print("  offline build log:", report["build_log"])
    if report["reason"]:
        print("  blocker:", report["reason"])
    print("Evidence:", destination)
    return 0 if report["status"] == "GATEWAY_OWNED_ACP_DOCTOR_PASS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
