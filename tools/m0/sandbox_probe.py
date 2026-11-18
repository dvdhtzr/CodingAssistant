#!/usr/bin/env python3
"""M0.4: verify rootless Podman isolation before any autonomous coding.

This is a negative-test harness. It never starts OpenCode or sends a model prompt.
No container image is downloaded automatically; an operator must first inspect
and pull a suitable Python image.
"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import platform
import shutil
import subprocess
import sys
import tempfile
from typing import Any

from common import ROOT, utcnow, write_json

DEFAULT_IMAGE = "docker.io/library/python:3.13-slim"

# Executed in an unprivileged, network-disconnected container. JSON is the
# only result; the host controller treats any missing/false check as failure.
CONTAINER_CHECKS = r"""
import json
import os
import pathlib
import socket
import sys

results = {}
results["non_root_identity"] = os.geteuid() != 0
try:
    status = pathlib.Path("/proc/self/status").read_text()
    fields = dict(line.split(":", 1) for line in status.splitlines() if ":" in line)
    results["no_effective_capabilities"] = int(fields.get("CapEff", "1").strip(), 16) == 0
    results["no_new_privileges"] = fields.get("NoNewPrivs", "0").strip() == "1"
except (OSError, ValueError):
    results["no_effective_capabilities"] = False
    results["no_new_privileges"] = False

try:
    pathlib.Path("/etc/codingassistant-m0-escape").write_text("not allowed")
    results["root_filesystem_read_only"] = False
except OSError:
    results["root_filesystem_read_only"] = True

try:
    socket.create_connection(("1.1.1.1", 443), timeout=1).close()
    results["external_network_denied"] = False
except OSError:
    results["external_network_denied"] = True

sentinel = pathlib.Path(sys.argv[1])
results["unmounted_host_file_inaccessible"] = not sentinel.exists()

try:
    probe = pathlib.Path("/workspace/probe.txt")
    probe.write_text("safe disposable worktree")
    results["workspace_write_available"] = probe.read_text() == "safe disposable worktree"
    probe.unlink()
except OSError:
    results["workspace_write_available"] = False

print(json.dumps({"checks": results}, sort_keys=True))
"""


def command_for(image: str, workspace: pathlib.Path, sentinel: pathlib.Path,
                uid: int, gid: int) -> list[str]:
    """Return deliberately narrow rootless Podman command (no shell expansion)."""
    return [
        "podman", "run", "--rm", "--pull=never",
        "--network=none",
        "--read-only",
        "--cap-drop=all",
        "--security-opt=no-new-privileges",
        "--pids-limit=64",
        "--memory=512m",
        "--userns=keep-id",
        "--user", f"{uid}:{gid}",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=64m",
        "--volume", f"{workspace.resolve()}:/workspace:rw",
        "--workdir=/workspace",
        image,
        "python3", "-c", CONTAINER_CHECKS, str(sentinel.resolve())
    ]


def checks_pass(payload: dict[str, Any]) -> bool:
    required = {
        "non_root_identity",
        "no_effective_capabilities",
        "no_new_privileges",
        "root_filesystem_read_only",
        "external_network_denied",
        "unmounted_host_file_inaccessible",
        "workspace_write_available",
    }
    checks = payload.get("checks")
    return isinstance(checks, dict) and set(checks) == required and all(
        checks[name] is True for name in required
    )


def inspect_podman(timeout: int) -> dict[str, Any]:
    cli = shutil.which("podman")
    if not cli:
        return {"available": False, "rootless": False, "reason": "Podman is not installed"}
    try:
        p = subprocess.run([cli, "info", "--format=json"],
                           capture_output=True, text=True, timeout=timeout, check=False)
        if p.returncode != 0:
            return {"available": True, "rootless": False,
                    "reason": "podman info failed", "stderr_tail": p.stderr[-600:]}
        info = json.loads(p.stdout)
        security = info.get("host", {}).get("security", {})
        rootless = security.get("rootless") is True
        return {"available": True, "rootless": rootless,
                "host_security": {k: security.get(k) for k in ("rootless", "seccompEnabled", "apparmorEnabled", "selinuxEnabled")},
                "reason": None if rootless else "Podman is not operating rootless"}
    except (ValueError, OSError, subprocess.TimeoutExpired) as exc:
        return {"available": True, "rootless": False, "reason": f"{type(exc).__name__}: {exc}"}


def image_status(image: str, timeout: int) -> dict[str, Any]:
    try:
        p = subprocess.run(["podman", "image", "inspect", image, "--format", "{{.Id}}"],
                           capture_output=True, text=True, check=False, timeout=timeout)
        return {"present": p.returncode == 0,
                "image_id": p.stdout.strip() if p.returncode == 0 else None}
    except (OSError, subprocess.TimeoutExpired) as exc:
        return {"present": False, "reason": f"{type(exc).__name__}: {exc}"}


def run_probe(image: str, timeout: int) -> dict[str, Any]:
    report: dict[str, Any] = {
        "schema_version": 1,
        "recorded_at_utc": utcnow(),
        "platform": platform.system(),
        "image": image,
        "status": "BLOCKED",
        "purpose": "M0 negative isolation preflight; not M1 security authorization",
    }
    if sys.platform != "linux" or not hasattr(os, "getuid"):
        report["reason"] = "Rootless Podman isolation probe is Linux-only"
        return report
    if os.geteuid() == 0:
        report["reason"] = "Do not run this as root or via sudo"
        return report
    report["podman"] = inspect_podman(timeout)
    if not report["podman"]["rootless"]:
        report["reason"] = report["podman"].get("reason") or "Rootless Podman unavailable"
        return report
    report["container_image"] = image_status(image, timeout)
    if not report["container_image"]["present"]:
        report["reason"] = "Container image is missing: inspect and explicitly pull it first"
        return report
    with tempfile.TemporaryDirectory(prefix="ca-m0-isolation-") as parent:
        root = pathlib.Path(parent)
        workspace = root / "workspace"
        workspace.mkdir()
        secret = root / "outside-sentinel"
        secret.write_text("DO NOT MOUNT", encoding="utf-8")
        cmd = command_for(image, workspace, secret, os.getuid(), os.getgid())
        try:
            p = subprocess.run(cmd, capture_output=True, text=True,
                               check=False, timeout=timeout)
        except (OSError, subprocess.TimeoutExpired) as exc:
            report["reason"] = f"Container probe did not complete: {type(exc).__name__}: {exc}"
            return report
        report["container_exit_code"] = p.returncode
        if p.returncode != 0:
            report["reason"] = "Isolated container returned a nonzero exit code"
            report["stderr_tail"] = p.stderr[-800:]
            return report
        try:
            output = json.loads(p.stdout.strip())
        except ValueError:
            report["reason"] = "Container did not return valid JSON"
            report["stdout_tail"] = p.stdout[-600:]
            return report
        report["checks"] = output.get("checks", {})
        if not checks_pass(output):
            report["reason"] = "Isolation negative checks failed"
            return report
    report["status"] = "PRECHECK_PASS"
    report["reason"] = None
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", default=DEFAULT_IMAGE)
    parser.add_argument("--timeout", type=int, default=100)
    parser.add_argument("--output", type=pathlib.Path,
                        default=ROOT / "artifacts/m0/sandbox-preflight.json")
    args = parser.parse_args()
    if args.timeout < 20 or args.timeout > 600:
        parser.error("--timeout must be 20 to 600 seconds")
    report = run_probe(args.image, args.timeout)
    write_json(args.output, report)
    print(f'M0.4 ROOTLESS ISOLATION {report["status"]}')
    for name, okay in sorted(report.get("checks", {}).items()):
        print(f'  {name}: {"PASS" if okay else "FAIL"}')
    if report.get("reason"):
        print("Reason:", report["reason"])
    print("Evidence:", args.output)
    print("No agent or model coding task was executed.")
    return 0 if report["status"] == "PRECHECK_PASS" else 2


if __name__ == "__main__":
    sys.exit(main())
