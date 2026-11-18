#!/usr/bin/env python3
"""M0.5: rootless container -> narrow Ollama Unix-socket broker feasibility probe.

No agent, arbitrary scripts, model inference requests, or third-party networking.
Existing preflight must be passed separately before this command.
"""
from __future__ import annotations
import argparse
import json
import os
import pathlib
import platform
import subprocess
import sys
import tempfile
from typing import Any

from common import ROOT, utcnow, write_json
from sandbox_probe import DEFAULT_IMAGE, image_status, inspect_podman
from ollama_bridge import serve_bridge

CONTAINER_PROBE = r"""
import json
import os
import pathlib
import socket
import sys

socket_path, sentinel = sys.argv[1:3]

def request(method, path):
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as s:
        s.settimeout(12)
        s.connect(socket_path)
        message = f"{method} {path} HTTP/1.0\r\nHost: ollama-bridge\r\nConnection: close\r\n\r\n"
        s.sendall(message.encode("ascii"))
        result = bytearray()
        while True:
            part = s.recv(16384)
            if not part:
                break
            result.extend(part)
            if len(result) > 1500000:
                raise ValueError("Bridge sent oversized response")
    head, _, body = bytes(result).partition(b"\r\n\r\n")
    first = head.splitlines()[0].decode("ascii", errors="replace")
    status = int(first.split(" ")[1])
    return status, body

checks = {"non_root_identity": os.geteuid() != 0}
try:
    status, data = request("GET", "/api/version")
    checks["metadata_version_available"] = status == 200 and bool(json.loads(data).get("version"))
except Exception:
    checks["metadata_version_available"] = False
try:
    status, data = request("GET", "/api/tags")
    checks["metadata_tags_available"] = status == 200 and isinstance(json.loads(data).get("models"), list)
except Exception:
    checks["metadata_tags_available"] = False
try:
    status, _ = request("POST", "/api/delete")
    checks["non_metadata_requests_denied"] = status == 403
except Exception:
    checks["non_metadata_requests_denied"] = False
checks["unmounted_sentinel_unavailable"] = not pathlib.Path(sentinel).exists()

try:
    pathlib.Path("/workspace/bridge-probe.tmp").write_text("ok")
    checks["workspace_writable"] = pathlib.Path("/workspace/bridge-probe.tmp").read_text() == "ok"
    pathlib.Path("/workspace/bridge-probe.tmp").unlink()
except OSError:
    checks["workspace_writable"] = False

try:
    socket.create_connection(("1.1.1.1", 443), timeout=1).close()
    checks["external_tcp_denied"] = False
except OSError:
    checks["external_tcp_denied"] = True

# HTTP-capable OpenCode requires a local TCP endpoint. Probe that separately;
# never silently enable broader network connectivity to make it work.
try:
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.settimeout(2)
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    with socket.create_connection(("127.0.0.1", listener.getsockname()[1]), timeout=2) as c:
        accepted, _ = listener.accept()
        accepted.close()
    listener.close()
    loopback_available = True
except OSError:
    loopback_available = False

print(json.dumps({"checks": checks, "loopback_tcp_available": loopback_available}, sort_keys=True))
"""

REQUIRED = frozenset((
    "non_root_identity", "metadata_version_available", "metadata_tags_available",
    "non_metadata_requests_denied", "unmounted_sentinel_unavailable",
    "workspace_writable", "external_tcp_denied"
))


def evaluate(output: dict[str, Any]) -> dict[str, Any]:
    checks = output.get("checks")
    if not isinstance(checks, dict) or set(checks) != REQUIRED:
        return {"status": "BLOCKED", "reason": "Incomplete bridge check set"}
    if not all(checks[key] is True for key in REQUIRED):
        return {"status": "BLOCKED", "reason": "One or more isolation/bridge checks failed"}
    if output.get("loopback_tcp_available") is not True:
        return {"status": "BRIDGE_ONLY", "reason": "Unix bridge works; TCP loopback unavailable under network=none"}
    return {"status": "PRECHECK_PASS", "reason": None}


def command_for(image: str, workspace: pathlib.Path, bridge_dir: pathlib.Path,
                sentinel: pathlib.Path, uid: int, gid: int) -> list[str]:
    return ["podman", "run", "--rm", "--pull=never",
            "--network=none", "--read-only", "--cap-drop=all",
            "--security-opt=no-new-privileges",
            "--pids-limit=64", "--memory=512m",
            "--userns=keep-id", "--user", f"{uid}:{gid}",
            "--tmpfs=/tmp:rw,nosuid,nodev,size=64m",
            "--volume", f"{workspace.resolve()}:/workspace:rw",
            "--volume", f"{bridge_dir.resolve()}:/run/codingassistant:ro",
            "--workdir=/workspace", image, "python3", "-c", CONTAINER_PROBE,
            "/run/codingassistant/ollama.sock", str(sentinel.resolve())]


def run_probe(image: str, timeout: int) -> dict[str, Any]:
    report: dict[str, Any] = {"schema_version": 1, "recorded_at_utc": utcnow(),
                              "status": "BLOCKED", "purpose": "M0 bridge preflight, not agent authorization",
                              "image": image, "platform": platform.system()}
    if sys.platform != "linux" or os.geteuid() == 0:
        report["reason"] = "Run on Linux as the normal non-root user"
        return report
    report["podman"] = inspect_podman(min(timeout, 20))
    if report["podman"].get("rootless") is not True:
        report["reason"] = "Podman rootless prerequisites missing"
        return report
    report["container_image"] = image_status(image, min(timeout, 20))
    if not report["container_image"].get("present"):
        report["reason"] = "Local Python container image missing; no automatic downloads"
        return report
    with tempfile.TemporaryDirectory(prefix="ca-m0-bridge-") as folder:
        root = pathlib.Path(folder)
        workspace = root / "workspace"
        workspace.mkdir(mode=0o700)
        private = root / "private"
        private.mkdir(mode=0o700)
        sentinel = root / "outside-sentinel"
        sentinel.write_text("never mounted", encoding="utf-8")
        with serve_bridge(private / "ollama.sock"):
            cmd = command_for(image, workspace, private, sentinel, os.getuid(), os.getgid())
            try:
                child = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                       text=True, encoding="utf-8", errors="replace",
                                       check=False, timeout=timeout)
            except (OSError, subprocess.TimeoutExpired) as exc:
                report["reason"] = f"Podman execution failed: {type(exc).__name__}: {exc}"
                return report
        report["container_exit_code"] = child.returncode
        if child.returncode != 0:
            report["reason"] = "Container probe returned nonzero exit code"
            report["stderr_tail"] = child.stderr[-600:]
            return report
        try:
            results = json.loads(child.stdout.strip())
        except ValueError:
            report["reason"] = "Container probe output is not valid JSON"
            return report
        report["checks"] = results.get("checks", {})
        report["loopback_tcp_available"] = results.get("loopback_tcp_available")
        report.update(evaluate(results))
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", default=DEFAULT_IMAGE)
    parser.add_argument("--timeout", type=int, default=90)
    parser.add_argument("--output", type=pathlib.Path, default=ROOT / "artifacts/m0/bridge-preflight.json")
    args = parser.parse_args()
    if args.timeout < 20 or args.timeout > 600:
        parser.error("Timeout must be between 20 and 600 seconds")
    result = run_probe(args.image, args.timeout)
    write_json(args.output, result)
    print(f'M0.5 LOCAL OLLAMA BRIDGE {result["status"]}')
    for name, passed in sorted(result.get("checks", {}).items()):
        print(f'  {name}: {"PASS" if passed else "FAIL"}')
    if "loopback_tcp_available" in result:
        print("  loopback_tcp_available:", result["loopback_tcp_available"])
    if result.get("reason"):
        print("Reason:", result["reason"])
    print("Evidence:", args.output)
    print("No autonomous coding or model inference request was executed.")
    return 0 if result["status"] == "PRECHECK_PASS" else 2


if __name__ == "__main__":
    sys.exit(main())
