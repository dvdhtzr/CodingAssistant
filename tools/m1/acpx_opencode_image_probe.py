#!/usr/bin/env python3
"""M1.7 — offline compose pinned OpenCode into ACPX Gateway, then read-only RPC.

--prepare explicitly builds a new local rootless image from two ALREADY-local
immutable IDs, with network=none/pull=never and NO Dockerfile RUN or package
installation. Without --prepare only existing-image verification runs.

Three verification layers:
  1) M0's proven OpenCode 2.3.0 image and M1.4 ACPX image provenance;
  2) child image Node/OpenClaw/ACPX + OpenCode 2.3.0 ACP CLI help offline;
  3) ACPX installed-opencode registry and actual Gateway metadata RPC
     from a temporary no-network/no-host-mount Gateway.
NO ACP session, model, cloud inference, coding task, or original worktree.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
import secrets

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/m0"))
from runtime_image_probe import (evaluate as m0_evaluate,
                                 IMAGE as M0_IMAGE,
                                 VERSION_PIN as OPENCODE_VERSION)
from acpx_gateway_activation_probe import IMAGE_ID as GATEWAY_IMAGE_ID
from acpx_opencode_rpc_probe import (REGISTRY_JS, RPC_METHOD,
                                     parse_registry, parse_rpc)
from acpx_source_image import (command, inspect_derived,
                               verify_prerequisites, verify_offline)
from gateway_loopback_probe import NODE_HTTP, parse_codes

ARTIFACTS = ROOT / "artifacts/m1"
CONTEXT = ROOT / "infra/m1"
CONTAINERFILE = CONTEXT / "Containerfile.opencode-acpx"
IMAGE_TAG = "localhost/codingassistant-gateway-acpx-opencode:2026.11.5-2.3.0"
IMAGE_ID = re.compile(r"(?:sha256:)?([0-9a-f]{64})\Z")
LABELS = {
    "io.github.dvdhtzr.codingassistant.composition": "m1.7-offline",
    "io.github.dvdhtzr.codingassistant.gateway_version": "2026.11.5",
    "io.github.dvdhtzr.codingassistant.acpx_version": "0.21.0",
    "io.github.dvdhtzr.codingassistant.opencode_version": OPENCODE_VERSION,
}
PORT = 18789


def m0_verified_id() -> str:
    evidence = m0_evaluate(False, 1800)
    if (evidence.get("status") != "TOOLCHAIN_PASS" or
            not all((evidence.get("checks") or {}).values())):
        raise ValueError("Existing pinned M0 rootless/offline image is not qualified")
    raw = evidence.get("image_id")
    match = IMAGE_ID.fullmatch(raw or "")
    if match is None:
        raise ValueError("M0 image has no immutable image identifier")
    return match.group(1)


def verify_inputs() -> str:
    verify_prerequisites()
    parent = inspect_derived()
    if parent != GATEWAY_IMAGE_ID:
        raise ValueError("Pinned M1.4 Gateway+ACPX base image changed")
    acpx = verify_offline(parent)
    if acpx["acpx_metadata"]["manifest"] != "acpx":
        raise ValueError("Verified source Gateway image lacks ACPX")
    return m0_verified_id()


def build_command(m0_id: str) -> list[str]:
    if not IMAGE_ID.fullmatch(m0_id) or m0_id == GATEWAY_IMAGE_ID:
        raise ValueError("M0 base image ID invalid")
    if not CONTAINERFILE.is_file() or CONTAINERFILE.is_symlink():
        raise ValueError("Trusted repo Containerfile is missing")
    args = [
        "podman", "build", "--pull=never", "--network=none",
        "--file", str(CONTAINERFILE), "--tag", IMAGE_TAG,
        "--build-arg", "M0_IMAGE=" + m0_id,
        "--build-arg", "GATEWAY_IMAGE=" + GATEWAY_IMAGE_ID,
    ]
    for key, value in sorted({
            **LABELS,
            "io.github.dvdhtzr.codingassistant.m0_image_id": m0_id,
            "io.github.dvdhtzr.codingassistant.gateway_image_id": GATEWAY_IMAGE_ID,
    }.items()):
        args.extend(("--label", key + "=" + value))
    args.append(str(CONTEXT))
    return args


def inspect_composed(m0_id: str) -> str | None:
    image = command(["podman", "image", "inspect", IMAGE_TAG], timeout=25)
    if image.returncode != 0:
        return None
    response = json.loads(image.stdout)
    if type(response) is not list or len(response) != 1 or (
            type(response[0]) is not dict):
        raise ValueError("Unexpected composed image inspection response")
    meta = response[0]
    match = IMAGE_ID.fullmatch(meta.get("Id", ""))
    if match is None:
        raise ValueError("Composed image lacks immutable digest")
    image_id = match.group(1)
    if image_id in (m0_id, GATEWAY_IMAGE_ID):
        raise ValueError("Composed image must not alias either parent")
    expected = {
        **LABELS,
        "io.github.dvdhtzr.codingassistant.m0_image_id": m0_id,
        "io.github.dvdhtzr.codingassistant.gateway_image_id": GATEWAY_IMAGE_ID,
    }
    labels = meta.get("Labels")
    if type(labels) is not dict or any(labels.get(key) != value
                                       for key, value in expected.items()):
        raise ValueError("Composed image input provenance labels mismatch")
    if IMAGE_TAG not in meta.get("RepoTags", []):
        raise ValueError("Composed image tag does not identify pinned image")
    return image_id


def guest(image_id: str, args: tuple[str, ...]) -> list[str]:
    if IMAGE_ID.fullmatch(image_id) is None or args not in (
        ("--version",),
        ("/app/openclaw.mjs", "--version"),
        ("--input-type=module", "-e", REGISTRY_JS),
        ("-e", "process.exitCode=0"),
        ("/usr/local/bin/opencode", "--version"),
        ("/usr/local/bin/opencode", "acp", "--help"),
    ):
        raise ValueError("Guest inspection command not allowlisted")
    uid, gid = os.getuid(), os.getgid()
    if uid <= 0 or gid <= 0:
        raise ValueError("Refuse root guest")
    return [
        "podman", "run", "--rm", "--pull=never", "--network=none",
        "--read-only", "--cap-drop=all", "--security-opt=no-new-privileges",
        "--userns=keep-id", f"--user={uid}:{gid}", "--pids-limit=64",
        "--memory=2g",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=256m,notmpcopyup",
        "--env=HOME=/tmp", "--env=XDG_CONFIG_HOME=/tmp/config",
        "--env=OPENCODE_DISABLE_AUTOUPDATE=1",
        "--workdir=/app", "--entrypoint=node", image_id, *args,
    ]


def binary_guest(image_id: str, args: tuple[str, ...]) -> list[str]:
    # OpenCode is a native script/binary, not a Node subcommand. Preserve
    # all guest security controls from the fixed Node-only probes.
    if args not in (("--version",), ("acp", "--help")):
        raise ValueError("Only OpenCode CLI self-report commands allowed")
    base = guest(image_id, ("/usr/local/bin/opencode", *args))
    idx = base.index("--entrypoint=node")
    base[idx] = "--entrypoint=/usr/local/bin/opencode"
    return base[:idx + 2] + list(args)


def fixed_node_guest(image_id: str, args: tuple[str, ...]) -> subprocess.CompletedProcess[str]:
    return command(guest(image_id, args), timeout=110)


def verify_combined(image_id: str) -> dict:
    results = {}
    for key, args in (
        ("node_version", ("--version",)),
        ("openclaw_version", ("/app/openclaw.mjs", "--version")),
        ("opencode_version", ("--version",)),
        ("acp_help", ("acp", "--help")),
        ("registry", ("--input-type=module", "-e", REGISTRY_JS)),
    ):
        if key in ("opencode_version", "acp_help"):
            outcome = command(binary_guest(image_id, args), timeout=120)
        else:
            outcome = fixed_node_guest(image_id, args)
        if outcome.returncode != 0:
            raise ValueError(key + " did not execute in sealed offline image")
        if key == "registry":
            results[key] = parse_registry(outcome.stdout)
        elif key == "acp_help":
            # Do not persist raw help text: its presence is what matters.
            results[key] = "acp" in outcome.stdout.lower() and (
                "usage" in outcome.stdout.lower() or
                "opencode" in outcome.stdout.lower())
        else:
            matches = re.findall(
                r"(?<![\w.])v?\d+\.\d+\.\d+(?![\w.])",
                (outcome.stdout + "\n" + outcome.stderr)[:1200])
            results[key] = matches[0] if matches else None
    if (results["openclaw_version"] != "2026.11.5" or
            results["opencode_version"] not in (OPENCODE_VERSION,
                                               "v" + OPENCODE_VERSION) or
            results["node_version"] != "v24.22.1" or
            results["acp_help"] is not True or
            results["registry"] != {
                "id": "opencode", "installation": "installed",
                "expectedCommand": True,
            }):
        raise ValueError("Derived image OpenCode/ACPX compatibility mismatch")
    return results


def ephemeral_gateway(image_id: str, name: str,
                      envfile: pathlib.Path) -> list[str]:
    if IMAGE_ID.fullmatch(image_id) is None or (
            re.fullmatch(r"ca-m17-acpx-[0-9a-f]{12}", name) is None):
        raise ValueError("Bad image or disposable container ID")
    if not envfile.is_file() or envfile.is_symlink() or (
            os.name == "posix" and envfile.stat().st_mode & 0o077):
        raise ValueError("Private Gateway token envfile required")
    uid, gid = os.getuid(), os.getgid()
    if uid <= 0 or gid <= 0:
        raise ValueError("Root Gateway not allowed")
    return [
        "podman", "run", "--detach", "--rm", "--pull=never",
        "--name", name, "--network=none", "--read-only",
        "--cap-drop=all", "--security-opt=no-new-privileges",
        "--userns=keep-id", f"--user={uid}:{gid}",
        "--pids-limit=96", "--memory=2g", "--no-healthcheck",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup",
        "--env-file", str(envfile), "--env=HOME=/tmp",
        "--env=XDG_CONFIG_HOME=/tmp/config",
        "--env=OPENCLAW_STATE_DIR=/tmp/state",
        "--env=OPENCLAW_NO_RESPAWN=1",
        "--env=OPENCLAW_SKIP_ACPX_RUNTIME_PROBE=1",
        "--env=OPENCODE_DISABLE_AUTOUPDATE=1",
        "--workdir=/app", "--entrypoint=node", image_id,
        "/app/openclaw.mjs", "gateway",
        "--allow-unconfigured", "--bind", "loopback", "--port", str(PORT),
    ]


def rpc_installed(image_id: str) -> dict:
    from gateway_image_probe import fixed
    evidence = {"gateway_ready": False, "adapter": None, "removed": False}
    name = "ca-m17-acpx-" + secrets.token_hex(6)
    try:
        with tempfile.TemporaryDirectory(prefix="ca-m17-private-") as temporary:
            envfile = pathlib.Path(temporary) / "gateway.env"
            descriptor = os.open(envfile, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
            with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
                stream.write("OPENCLAW_GATEWAY_TOKEN=" +
                             secrets.token_hex(32) + "\n")
                stream.write("OPENCLAW_SKIP_CHANNELS=1\n")
                stream.write("OPENCLAW_SKIP_CANVAS_HOST=1\n")
            starting = fixed(ephemeral_gateway(image_id, name, envfile), timeout=40)
            if starting.returncode != 0:
                raise ValueError("Temporary composed-image Gateway startup failed")
            ready = False
            for _ in range(30):
                alive = fixed(["podman", "inspect", "--format",
                               "{{.State.Running}}", name], timeout=12)
                if alive.returncode != 0 or alive.stdout.strip() != "true":
                    raise ValueError("Temporary composed Gateway exited")
                response = fixed(["podman", "exec", "--interactive=false",
                                  name, "node", "-e", NODE_HTTP], timeout=20)
                if response.returncode != 0:
                    raise ValueError("Fixed in-container health probe failed")
                codes = parse_codes(response.stdout)
                if all(codes[k] == 200 for k in (
                        "/healthz", "/startupz", "/readyz")):
                    ready = True
                    break
                time.sleep(2)
            if not ready:
                raise ValueError("Composed Gateway did not reach full readiness")
            evidence["gateway_ready"] = True
            response = fixed([
                "podman", "exec", "--interactive=false", name, "node",
                "/app/openclaw.mjs", "gateway", "call",
                RPC_METHOD, "--params", "{}", "--json", "--timeout", "15000",
            ], timeout=35)
            if response.returncode != 0:
                raise ValueError("ACPX read-only Gateway RPC unavailable")
            descriptor = parse_rpc(response.stdout)
            if descriptor != {
                "id": "opencode", "runtimeId": "acp-opencode",
                "installation": "installed", "enabled": True,
            }:
                raise ValueError("Gateway does not recognize installed pinned OpenCode ACP")
            evidence["adapter"] = descriptor
    finally:
        # Unconditional cleanup. A failed cleanup blocks the entire gate.
        removed = fixed(["podman", "rm", "-f", name], timeout=25)
        leftover = fixed(["podman", "container", "exists", name], timeout=15)
        evidence["removed"] = leftover.returncode == 1
        if not evidence["removed"]:
            raise ValueError("Temporary Gateway cleanup not verified")
    return evidence


def evaluate(prepare: bool = False) -> dict:
    result = {
        "schema_version": 1, "stage": "M1.7-offline-opencode-acpx",
        "recorded_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "BLOCKED", "prepare_requested": prepare,
        "m0_image_id": None, "parent_acpx_image_id": GATEWAY_IMAGE_ID,
        "derived_image_id": None, "local_tag": IMAGE_TAG,
        "offline": None, "rpc": None,
        "no_network_in_build": True, "no_network_at_runtime": True,
        "no_host_mounts": True, "host_node_modified": False,
        "m0_image_modified": False, "acpx_parent_modified": False,
        "npx_fallback_executed": False, "agent_spawned": False,
        "acp_session_started": False, "model_inference": False,
        "build_log": None, "reason": None,
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        result["reason"] = "Linux rootless non-root Podman required"
        return result
    try:
        m0_id = verify_inputs()
        result["m0_image_id"] = m0_id
        if prepare:
            if shutil.disk_usage(ROOT).free < 4 * 1024**3:
                raise ValueError("At least 4 GiB free required for offline image composition")
            ARTIFACTS.mkdir(parents=True, exist_ok=True)
            log = ARTIFACTS / ("acpx-opencode-build-" + uuid.uuid4().hex[:12] + ".log")
            result["build_log"] = str(log)
            built = command(build_command(m0_id), timeout=1500, log=log)
            if built.returncode != 0:
                raise ValueError("Offline Podman image composition failed; inspect ignored build log")
        image_id = inspect_composed(m0_id)
        if image_id is None:
            raise ValueError("Composed offline image absent; rerun with --prepare")
        result["derived_image_id"] = image_id
        result["offline"] = verify_combined(image_id)
        result["rpc"] = rpc_installed(image_id)
        result["status"] = "ACPX_OPENCODE_INSTALLED_DISCOVERY_PASS"
    except (ValueError, KeyError, TypeError, OSError,
            subprocess.TimeoutExpired, json.JSONDecodeError) as exc:
        result["reason"] = str(exc)[:210]
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare", action="store_true",
                        help="Explicitly compose two locally pinned images, no network/downloads")
    args = parser.parse_args()
    result = evaluate(args.prepare)
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    record = ARTIFACTS / ("acpx-opencode-installed-" + uuid.uuid4().hex[:12] + ".json")
    fd = os.open(record, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as output:
        json.dump(result, output, indent=2, sort_keys=True)
        output.write("\n")
    print("M1.7", result["status"])
    print("  existing M0 image:", result["m0_image_id"] or "NOT_VERIFIED")
    print("  composed immutable image:", result["derived_image_id"] or "NOT_VERIFIED")
    print("  offline OpenCode/ACPX:", result["offline"])
    print("  Gateway read-only OpenCode registry:", result["rpc"])
    print("  internet/ACP session/model/agent execution: NOT_ATTEMPTED")
    print("  host Node and prior images unchanged: YES")
    if result["build_log"]:
        print("  local build log:", result["build_log"])
    if result["reason"]:
        print("  blocker:", result["reason"])
    print("Evidence:", record)
    return 0 if result["status"] == "ACPX_OPENCODE_INSTALLED_DISCOVERY_PASS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
