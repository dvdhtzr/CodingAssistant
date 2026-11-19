#!/usr/bin/env python3
"""M1.5: genuine ACPX plugin registration inside a temporary isolated Gateway.

For the exact owner-verified, source-built OpenClaw + ACPX image, start one
ephemeral rootless/no-network Gateway; require HTTP liveness/readiness and
the upstream pinned plugin's LAZY backend-registration startup signal.
Do not invoke an ACP method, spawn an agent, read host config, mount source,
call Ollama/cloud, publish ports, or install any package. Stop the container
on ALL paths. Full ACP compatibility and security remain NOT QUALIFIED.
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

from acpx_source_image import (
    IMAGE_TAG, SOURCE_COMMIT, inspect_derived, verify_offline,
    verify_prerequisites,
)
from gateway_image_probe import fixed
from gateway_loopback_probe import NODE_HTTP, parse_codes

ROOT = pathlib.Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts/m1"
IMAGE_ID = "5aa8b2911525ccf1d5af5ba73ae4bc5f219cdf2a7c91bdf6cfee7208941c5f0c"
REGISTRATION = "embedded acpx runtime backend registered lazily"
PORT = 18789


def gateway_command(image_id: str, name: str, uid: int, gid: int,
                    envfile: pathlib.Path) -> list[str]:
    if image_id != IMAGE_ID or uid <= 0 or gid <= 0:
        raise ValueError("Expected owner's immutable derived image and non-root identity")
    if not name.startswith("ca-m15-acpx-") or len(name) > 42:
        raise ValueError("Unapproved disposable container name")
    if (not envfile.is_file() or envfile.is_symlink() or
            os.name == "posix" and envfile.stat().st_mode & 0o077):
        raise ValueError("Gateway token file is not a regular owner-only file")
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
        "--env=OPENCLAW_ACPX_RUNTIME_STARTUP_PROBE=0",
        "--env=OPENCLAW_SKIP_ACPX_RUNTIME_PROBE=1",
        "--workdir=/app", "--entrypoint=node", IMAGE_ID,
        "/app/openclaw.mjs", "gateway",
        "--allow-unconfigured", "--bind", "loopback", "--port", str(PORT),
    ]


def run() -> dict:
    report = {
        "schema_version": 1, "stage": "M1.5-acpx-gateway-lazy-registration",
        "recorded_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "BLOCKED",
        "source_commit": SOURCE_COMMIT,
        "source_image_tag": IMAGE_TAG, "image_id": None,
        "rootless": False, "offline_image_rechecked": False,
        "network_disabled": True, "host_mounts": False,
        "published_ports": False,
        "gateway_started": False, "health_codes": None,
        "acpx_lazy_registration_observed": False,
        "gateway_removed": False,
        "acp_session_started": False, "adapter_spawn_requested": False,
        "model_inference_requested": False,
        "host_files_modified": False, "reason": None,
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        report["reason"] = "Linux rootless non-root Podman required"
        return report

    # Generate only a local disposable container name, never from user text.
    name = "ca-m15-acpx-" + secrets.token_hex(6)
    try:
        verify_prerequisites()
        report["rootless"] = True
        image_id = inspect_derived()
        if image_id != IMAGE_ID:
            raise ValueError("Derived ACPX image changed from owner M1.4 verified ID")
        report["image_id"] = image_id
        verified = verify_offline(image_id)
        if verified["acpx_metadata"]["manifest"] != "acpx":
            raise ValueError("Pinned ACPX plugin metadata no longer present")
        report["offline_image_rechecked"] = True

        with tempfile.TemporaryDirectory(prefix="ca-m15-gateway-") as private:
            path = pathlib.Path(private) / "gateway.env"
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8") as stream:
                stream.write("OPENCLAW_GATEWAY_TOKEN=" +
                             secrets.token_hex(32) + "\n")
                stream.write("OPENCLAW_SKIP_CHANNELS=1\n")
                stream.write("OPENCLAW_SKIP_CANVAS_HOST=1\n")
            start = fixed(gateway_command(
                image_id, name, os.getuid(), os.getgid(), path),
                timeout=40)
            if start.returncode != 0:
                raise ValueError("Pinned ACPX-enabled Gateway failed to start")
            report["gateway_started"] = True
            ready = False
            for _ in range(30):
                alive = fixed(["podman", "inspect", "--format",
                               "{{.State.Running}}", name], timeout=12)
                if alive.returncode != 0 or alive.stdout.strip() != "true":
                    raise ValueError("Gateway exited before plugin registration check")
                health = fixed(["podman", "exec", "--interactive=false",
                                name, "node", "-e", NODE_HTTP], timeout=20)
                if health.returncode != 0:
                    raise ValueError("Fixed in-guest health probe failed")
                codes = parse_codes(health.stdout)
                report["health_codes"] = codes
                if all(codes[endpoint] == 200 for endpoint in (
                        "/healthz", "/startupz", "/readyz")):
                    ready = True
                    break
                time.sleep(2)
            if not ready:
                raise ValueError("Gateway never reached full loopback readiness")

            # Never persist or print log contents: they may contain credentials.
            # Only the exact, pinned, upstream plugin registration phrase
            # is admitted as a boolean observation.
            logs = fixed(["podman", "logs", "--tail", "1200", name],
                         timeout=20)
            if logs.returncode != 0:
                raise ValueError("Cannot inspect Gateway's ACPX registration evidence")
            report["acpx_lazy_registration_observed"] = (
                REGISTRATION in (logs.stdout + logs.stderr))
            if not report["acpx_lazy_registration_observed"]:
                raise ValueError("ACPX lazy backend registration not observed in Gateway")
            report["status"] = "ACPX_LAZY_REGISTRATION_PASS"
    except (OSError, ValueError, TypeError, KeyError,
            subprocess.TimeoutExpired) as exc:
        report["reason"] = str(exc)[:190]
    finally:
        # Even if preflight fails, only this randomly named container is
        # targeted. Never stop another OpenClaw instance or a host service.
        try:
            fixed(["podman", "rm", "-f", name], timeout=25)
            exists = fixed(["podman", "container", "exists", name], timeout=15)
            report["gateway_removed"] = exists.returncode == 1
            if not report["gateway_removed"]:
                report["status"] = "BLOCKED"
                report["reason"] = "Disposable ACPX Gateway cleanup not verified"
        except (OSError, subprocess.TimeoutExpired):
            report["gateway_removed"] = False
            report["status"] = "BLOCKED"
            report["reason"] = "Disposable ACPX Gateway cleanup failed"
    if not report["gateway_removed"]:
        report["status"] = "BLOCKED"
    return report


def main() -> int:
    result = run()
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    target = ARTIFACTS / ("acpx-lazy-gateway-" + uuid.uuid4().hex[:12] + ".json")
    fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2, sort_keys=True)
        stream.write("\n")
    print("M1.5", result["status"])
    print("  verified image:", result["image_id"] or "NOT_AVAILABLE")
    print("  offline ACPX metadata recheck:", result["offline_image_rechecked"])
    print("  Gateway started:", result["gateway_started"])
    print("  health:", result["health_codes"])
    print("  plugin backend registered lazily:",
          result["acpx_lazy_registration_observed"])
    print("  disposable Gateway removed:", result["gateway_removed"])
    print("  ACP session / model / adapter launch: NOT_ATTEMPTED")
    if result["reason"]:
        print("  blocker:", result["reason"])
    print("Evidence:", target)
    return 0 if result["status"] == "ACPX_LAZY_REGISTRATION_PASS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
