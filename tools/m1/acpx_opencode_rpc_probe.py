#!/usr/bin/env python3
"""M1.6: authenticated read-only Gateway -> ACPX OpenCode adapter discovery.

The real ACPX-enabled Gateway starts only inside a disposable, rootless,
network=none Podman container. Query exactly ONE upstream plugin RPC:
'acpx.agents.list' with empty params. Independently inspect the pinned
acpx v0.21.0 registry in a separate offline guest. Neither call may
start OpenCode, use npx, create an ACP session, invoke a model, or touch a
host workspace. Missing OpenCode is a valid discovery, NOT ACP readiness.
"""
from __future__ import annotations

import datetime as dt
import json
import os
import pathlib
import secrets
import subprocess
import sys
import tempfile
import time
import uuid

from acpx_gateway_activation_probe import IMAGE_ID, gateway_command
from acpx_source_image import inspect_derived, verify_offline, verify_prerequisites
from gateway_image_probe import fixed
from gateway_loopback_probe import NODE_HTTP, parse_codes

ROOT = pathlib.Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts/m1"
AGENT = "opencode"
PORT = 18789
# The exact upstream OpenClaw v2026.11.5 plugin registers this method
# as operator.read. This request has no side effects or agent commands.
RPC_METHOD = "acpx.agents.list"
REGISTRY_JS = r"""
(async () => {
  const { createAgentRegistry } = await import('acpx/agent-registry');
  const { basename } = await import('node:path');
  const inspection = createAgentRegistry().inspect('opencode');
  if (!inspection || inspection.id !== 'opencode') {
    process.stdout.write(JSON.stringify({id:'opencode',installation:'unverified',expectedCommand:false})+'\\n');
    return;
  }
  const launch = inspection.launch;
  const command = launch.kind === 'installed' ? launch.argv : [];
  const expectedCommand = launch.kind === 'installed'
    ? command.length === 2 && basename(command[0]) === 'opencode' && command[1] === 'acp'
    : launch.kind === 'missing' && launch.requirements?.some(
        item => item.kind === 'command' && item.name === 'opencode');
  process.stdout.write(JSON.stringify({
    id:'opencode', installation:launch.kind, expectedCommand:Boolean(expectedCommand)
  })+'\\n');
})().catch(() => { process.exitCode = 2; });
"""


def registry_command(image_id: str, uid: int, gid: int) -> list[str]:
    if image_id != IMAGE_ID or uid <= 0 or gid <= 0:
        raise ValueError("Unexpected pinned image/non-root identity")
    return [
        "podman", "run", "--rm", "--pull=never", "--network=none",
        "--read-only", "--cap-drop=all",
        "--security-opt=no-new-privileges", "--userns=keep-id",
        f"--user={uid}:{gid}", "--pids-limit=64", "--memory=2g",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=128m,notmpcopyup",
        "--env=HOME=/tmp", "--env=XDG_CONFIG_HOME=/tmp/config",
        "--workdir=/app", "--entrypoint=node",
        image_id, "--input-type=module", "-e", REGISTRY_JS,
    ]


def rpc_command(name: str) -> list[str]:
    if not name.startswith("ca-m16-acpx-") or len(name) > 42:
        raise ValueError("Unapproved transient container name")
    # Uses the freshly generated token already in the disposable guest's
    # environment. Never send token as a CLI arg, read its value, or log it.
    # No explicit --url: the pinned CLI selects its own local Gateway.
    return [
        "podman", "exec", "--interactive=false", name,
        "node", "/app/openclaw.mjs", "gateway", "call",
        RPC_METHOD, "--params", "{}", "--json", "--timeout", "15000",
    ]


def parse_registry(raw: str) -> dict[str, object]:
    if len(raw) > 1500:
        raise ValueError("Registry report exceeds bound")
    item = json.loads(raw)
    if (type(item) is not dict or set(item) != {
            "id", "installation", "expectedCommand"} or
            item["id"] != AGENT or
            item["installation"] not in ("installed", "missing", "unverified") or
            type(item["expectedCommand"]) is not bool):
        raise ValueError("Invalid ACPX registry inspection")
    if not item["expectedCommand"]:
        raise ValueError("Missing or unexpected OpenCode command definition")
    return item


def parse_rpc(raw: str) -> dict[str, object]:
    if len(raw) > 12_000:
        raise ValueError("Plugin RPC output exceeds bound")
    result = json.loads(raw)
    if (type(result) is not dict or set(result) != {"agents"} or
            type(result["agents"]) is not list or len(result["agents"]) > 24):
        raise ValueError("Unexpected ACPX read-only RPC response")
    matches = [item for item in result["agents"] if
               type(item) is dict and item.get("id") == AGENT]
    if len(matches) != 1:
        raise ValueError("Expected exactly one OpenCode agent descriptor")
    target = matches[0]
    if (target.get("installation") not in ("installed", "missing", "unverified") or
            type(target.get("enabled")) is not bool or
            target.get("runtimeId") != "acp-opencode"):
        raise ValueError("Invalid OpenCode descriptor")
    return {
        "id": AGENT, "runtimeId": "acp-opencode",
        "installation": target["installation"], "enabled": target["enabled"]
    }


def run() -> dict:
    evidence: dict[str, object] = {
        "schema_version": 1, "stage": "M1.6-acpx-opencode-readonly-rpc",
        "recorded_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "BLOCKED", "image_id": None, "offline_packaging_verified": False,
        "registry": None, "gateway_started": False, "health_codes": None,
        "rpc_verified": False, "agent": None, "gateway_removed": False,
        "network_disabled": True, "host_mounts": False,
        "published_ports": False, "model_inference": False,
        "agent_spawned": False, "acp_session_started": False,
        "source_modified": False, "reason": None,
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        evidence["reason"] = "Linux non-root rootless Podman required"
        return evidence

    name = "ca-m16-acpx-" + secrets.token_hex(6)
    try:
        verify_prerequisites()
        image_id = inspect_derived()
        if image_id != IMAGE_ID:
            raise ValueError("M1.4 immutable Gateway+ACPX image changed")
        evidence["image_id"] = image_id
        packaged = verify_offline(image_id)
        if packaged["acpx_metadata"]["manifest"] != "acpx":
            raise ValueError("ACPX plugin no longer packaged")
        evidence["offline_packaging_verified"] = True

        # First observe the actual pinned runtime's registry without
        # invoking its launch argv. No npx/adapter process is executed.
        inspected = fixed(registry_command(
            image_id, os.getuid(), os.getgid()), timeout=90)
        if inspected.returncode != 0:
            raise ValueError("Offline ACPX registry import/inspection failed")
        registry = parse_registry(inspected.stdout)
        evidence["registry"] = registry

        with tempfile.TemporaryDirectory(prefix="ca-m16-gateway-") as temporary:
            envfile = pathlib.Path(temporary) / "gateway.env"
            fd = os.open(envfile, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8") as handle:
                handle.write("OPENCLAW_GATEWAY_TOKEN=" +
                             secrets.token_hex(32) + "\\n")
                handle.write("OPENCLAW_SKIP_CHANNELS=1\\n")
                handle.write("OPENCLAW_SKIP_CANVAS_HOST=1\\n")
            start = fixed(gateway_command(
                image_id, name, os.getuid(), os.getgid(), envfile), timeout=40)
            if start.returncode != 0:
                raise ValueError("Disposable Gateway startup failed")
            evidence["gateway_started"] = True

            ready = False
            for _ in range(30):
                alive = fixed(["podman", "inspect", "--format",
                               "{{.State.Running}}", name], timeout=12)
                if alive.returncode != 0 or alive.stdout.strip() != "true":
                    raise ValueError("Gateway exited during offline RPC discovery")
                health = fixed(["podman", "exec", "--interactive=false",
                                name, "node", "-e", NODE_HTTP], timeout=20)
                if health.returncode != 0:
                    raise ValueError("Guest health monitor failed")
                codes = parse_codes(health.stdout)
                evidence["health_codes"] = codes
                if all(codes[endpoint] == 200 for endpoint in (
                        "/healthz", "/startupz", "/readyz")):
                    ready = True
                    break
                time.sleep(2)
            if not ready:
                raise ValueError("Gateway never reached full loopback readiness")

            reply = fixed(rpc_command(name), timeout=35)
            if reply.returncode != 0:
                # CLI output may contain private device/gateway metadata.
                # Never include it in audit or printed diagnostic errors.
                raise ValueError(
                    "Read-only ACPX RPC refused (exit " +
                    str(reply.returncode) + "); no agent launched")
            descriptor = parse_rpc(reply.stdout)
            if descriptor["installation"] != registry["installation"]:
                raise ValueError("Gateway and independent ACPX registry disagree")
            evidence["agent"] = descriptor
            evidence["rpc_verified"] = True
            evidence["status"] = "ACPX_OPENCODE_DISCOVERY_PASS"
    except (ValueError, KeyError, OSError, TypeError,
            json.JSONDecodeError, subprocess.TimeoutExpired) as exc:
        evidence["reason"] = str(exc)[:190]
    finally:
        # Preserve the M1.5 fail-closed cleanup invariant, including
        # preflight failure. Never target another user's container.
        try:
            fixed(["podman", "rm", "-f", name], timeout=25)
            exists = fixed(["podman", "container", "exists", name], timeout=15)
            evidence["gateway_removed"] = exists.returncode == 1
            if not evidence["gateway_removed"]:
                evidence["status"] = "BLOCKED"
                evidence["reason"] = "Disposable Gateway cleanup not confirmed"
        except (OSError, subprocess.TimeoutExpired):
            evidence["gateway_removed"] = False
            evidence["status"] = "BLOCKED"
            evidence["reason"] = "Disposable Gateway cleanup failed"
    if not evidence["gateway_removed"]:
        evidence["status"] = "BLOCKED"
    return evidence


def main() -> int:
    result = run()
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    path = ARTIFACTS / ("acpx-opencode-rpc-" + uuid.uuid4().hex[:12] + ".json")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2, sort_keys=True)
        handle.write("\\n")
    print("M1.6", result["status"])
    print("  image:", result["image_id"] or "NOT_VERIFIED")
    print("  offline registry:", result["registry"])
    print("  Gateway ready:", result["health_codes"])
    print("  ACPX read-only RPC:", result["rpc_verified"])
    print("  OpenCode adapter:", result["agent"])
    print("  disposable Gateway removed:", result["gateway_removed"])
    print("  ACP session / agent / model: NOT_ATTEMPTED")
    if result["reason"]:
        print("  blocker:", result["reason"])
    print("Evidence:", path)
    return 0 if result["status"] == "ACPX_OPENCODE_DISCOVERY_PASS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
