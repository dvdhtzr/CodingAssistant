#!/usr/bin/env python3
"""M1.2 temporary real Gateway loopback liveness/readiness smoke.

Starts official, pinned OpenClaw in a disposable rootless, network=none
container with NO host mounts or published ports. Uses a fresh random Gateway
token, stored only in short-lived 0600 env-file and container lifetime.
Checks exact /healthz, /startupz, /readyz through loopback INSIDE the guest.
Never starts ACP sessions, invokes a model, or connects a remote service.
Always attempts removal. No unattended service is installed.
"""
from __future__ import annotations
import argparse
import datetime as dt
import json
import os
import pathlib
import secrets
import subprocess
import sys
import tempfile
import time

from gateway_image_probe import APPROVED_REPO_DIGEST, IMAGE, inspected_image, fixed

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "artifacts/m1/gateway-loopback-smoke.json"
APPROVED_IMAGE_ID = (
    "405dcc682726f127d316d435e29d3c5d0cdecb502aef6d74384ce6dd4ffe2700"
)
PORT = 18789
# Fixed, bounded, read-only HTTP probe; no token or provider credentials.
NODE_HTTP = r"""
(async () => {
  const out = {};
  for (const path of ['/healthz', '/startupz', '/readyz']) {
    const timer = AbortSignal.timeout(2500);
    try {
      const reply = await fetch('http://127.0.0.1:18789' + path, {
        signal: timer, redirect: 'error', cache: 'no-store'
      });
      out[path] = reply.status;
      await reply.body?.cancel();
    } catch {
      out[path] = null;
    }
  }
  process.stdout.write(JSON.stringify(out) + '\n');
})().catch(() => { process.exitCode = 2; });
"""


def gateway_command(image_id: str, name: str, uid: int, gid: int,
                    env_path: pathlib.Path) -> list[str]:
    if image_id != APPROVED_IMAGE_ID:
        raise ValueError("Gateway image differs from M1.1 immutable local image")
    if not name.startswith("ca-m12-gateway-") or len(name) > 45:
        raise ValueError("Invalid disposable container name")
    # Windows model-free CI can mock the Linux Podman call contract, but
    # NTFS's stat() permission bits are not an authoritative POSIX 0600
    # permission check. On actual Linux, require strict owner-only modes.
    if not env_path.is_file() or (
            os.name == "posix" and (env_path.stat().st_mode & 0o077)):
        raise ValueError("Temporary gateway environment is not private")
    return [
        "podman", "run", "--detach", "--rm", "--pull=never",
        "--name", name, "--network=none", "--read-only",
        "--cap-drop=all", "--security-opt=no-new-privileges",
        "--userns=keep-id", f"--user={uid}:{gid}",
        "--pids-limit=96", "--memory=2g", "--no-healthcheck",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup",
        "--env-file", str(env_path), "--env=HOME=/tmp",
        "--env=XDG_CONFIG_HOME=/tmp/config",
        "--env=OPENCLAW_STATE_DIR=/tmp/state",
        "--env=OPENCLAW_NO_RESPAWN=1",
        "--workdir=/app", "--entrypoint=node",
        image_id, "/app/openclaw.mjs", "gateway",
        "--allow-unconfigured", "--bind", "loopback",
        "--port", str(PORT),
    ]


def parse_codes(raw: str) -> dict[str, int | None]:
    parsed = json.loads(raw)
    if type(parsed) is not dict or set(parsed) != {
        "/healthz", "/startupz", "/readyz"
    }:
        raise ValueError("Unexpected HTTP probe response")
    if any(value is not None and
           (type(value) is not int or value not in (200, 503))
           for value in parsed.values()):
        raise ValueError("Unexpected HTTP status from loopback health probe")
    return parsed


def run() -> dict:
    report = {
        "schema_version": 1, "stage": "M1.2-gateway-loopback-smoke",
        "recorded_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "BLOCKED", "image_id": None,
        "repo_digest": None, "rootless": False,
        "no_network": True, "no_host_mounts": True,
        "no_published_ports": True, "ephemeral_config": True,
        "gateway_started": False, "gateway_removed": False,
        "acp_session_started": False, "model_inference": False,
        "host_node_modified": False, "original_source_modified": False,
        "health_codes": None, "reason": None,
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        report["reason"] = "Requires Linux non-root rootless Podman"
        return report
    container = "ca-m12-gateway-" + secrets.token_hex(6)
    running = False
    try:
        check = fixed(["podman", "info", "--format",
                       "{{.Host.Security.Rootless}}"], timeout=25)
        if check.returncode != 0 or check.stdout.strip() != "true":
            raise ValueError("Rootless Podman not verified")
        report["rootless"] = True
        known = inspected_image()
        if known is None or known["image_id"] != APPROVED_IMAGE_ID or (
                known["repo_digest"] != APPROVED_REPO_DIGEST):
            raise ValueError("Installed image does not match M1.1 verified pin")
        report.update(known)
        with tempfile.TemporaryDirectory(prefix="ca-m12-private-") as temp:
            envfile = pathlib.Path(temp) / "gateway.env"
            descriptor = os.open(envfile, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(descriptor, "w", encoding="utf-8") as file:
                file.write("OPENCLAW_GATEWAY_TOKEN=" + secrets.token_hex(32) + "\n")
                file.write("OPENCLAW_SKIP_CHANNELS=1\n")
                file.write("OPENCLAW_SKIP_CANVAS_HOST=1\n")
            args = gateway_command(known["image_id"], container, os.getuid(),
                                   os.getgid(), envfile)
            started = fixed(args, timeout=35)
            if started.returncode != 0:
                raise ValueError("Isolated Gateway container failed to start")
            running = True
            report["gateway_started"] = True
            latest = None
            # Readiness is distinct from PID liveness. Don't treat one
            # transient connection refusal as a failure before timeout.
            for _ in range(30):
                ps = fixed(["podman", "inspect", "--format",
                            "{{.State.Running}}", container], timeout=12)
                if ps.returncode != 0 or ps.stdout.strip() != "true":
                    raise ValueError("Isolated Gateway exited before readiness")
                probe = fixed(["podman", "exec", "--interactive=false",
                               container, "node", "-e", NODE_HTTP], timeout=20)
                if probe.returncode != 0:
                    raise ValueError("Fixed, inside-container HTTP probe failed")
                latest = parse_codes(probe.stdout)
                report["health_codes"] = latest
                if all(latest[k] == 200 for k in (
                        "/healthz", "/startupz", "/readyz")):
                    report["status"] = "GATEWAY_LOOPBACK_PASS"
                    break
                time.sleep(2)
            if report["status"] != "GATEWAY_LOOPBACK_PASS":
                raise ValueError("Gateway did not become ready inside isolated loopback")
    except (OSError, ValueError, KeyError, TypeError, json.JSONDecodeError,
            subprocess.TimeoutExpired) as exc:
        report["reason"] = str(exc)[:180]
    finally:
        # We never want a persistent autonomous service or exposed control
        # plane from a preflight. --rm also applies after exit.
        # Container name is a locally generated unguessable fixed prefix.
        try:
            stopped = fixed(["podman", "rm", "-f", container], timeout=25)
            remaining = fixed(
                ["podman", "container", "exists", container], timeout=15)
            report["gateway_removed"] = (remaining.returncode == 1)
            if not report["gateway_removed"]:
                report["status"] = "BLOCKED"
                report["reason"] = "Temporary Gateway container cleanup not verified"
        except (OSError, subprocess.TimeoutExpired):
            report["gateway_removed"] = False
            report["status"] = "BLOCKED"
            report["reason"] = "Temporary Gateway container cleanup failed"
    if not report["gateway_removed"]:
        report["status"] = "BLOCKED"
    return report


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.parse_args()
    result = run()
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n",
                      encoding="utf-8")
    print("M1.2", result["status"])
    for name in ("rootless", "no_network", "no_host_mounts",
                 "no_published_ports", "gateway_started", "gateway_removed"):
        print(f"  {name}: {'PASS' if result[name] else 'FAIL'}")
    print("  loopback health:", result["health_codes"])
    print("  ACP session/model inference: NOT_ATTEMPTED")
    if result["reason"]:
        print("  blocker:", result["reason"])
    print("Evidence:", OUTPUT)
    return 0 if result["status"] == "GATEWAY_LOOPBACK_PASS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
