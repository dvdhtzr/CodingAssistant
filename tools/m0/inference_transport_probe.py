#!/usr/bin/env python3
"""M0.6: prove bounded, pinned local Ollama inference over loopback TCP -> AF_UNIX.

Uses only a fixed Python probe in a rootless, network-none Podman container.
It NEVER starts OpenCode, accepts an arbitrary prompt, or runs model output.
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

from common import ROOT, ollama_json, utcnow, write_json
from ollama_bridge import serve_bridge
from pinned_inference import PROBE_PROMPT, pinned_handler
from sandbox_probe import DEFAULT_IMAGE, image_status, inspect_podman

CONTAINER_SCRIPT = r"""
import http.client
import http.server
import json
import os
import pathlib
import socket
import sys
import threading
import urllib.error
import urllib.request

sock_path, model, outside = sys.argv[1:4]
class UnixConnection(http.client.HTTPConnection):
    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(255)
        self.sock.connect(sock_path)

class LocalProxy(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass
    def do_GET(self):
        self.forward()
    def do_POST(self):
        self.forward()
    def forward(self):
        if self.path not in ("/api/version", "/api/tags", "/api/chat"):
            self.send_error(403)
            return
        length = self.headers.get("Content-Length", "0")
        if not length.isascii() or not length.isdecimal() or int(length) > 2048:
            self.send_error(403)
            return
        if self.command == "POST" and self.path != "/api/chat":
            self.send_error(403)
            return
        if self.command == "GET" and self.path == "/api/chat":
            self.send_error(403)
            return
        data = self.rfile.read(int(length))
        upstream = UnixConnection("local", timeout=255)
        try:
            upstream.request(self.command, self.path, body=data, headers={
                "Content-Type": "application/json", "Connection": "close"})
            resp = upstream.getresponse()
            payload = resp.read(262145)
            if len(payload) > 262144:
                self.send_error(502)
                return
            self.send_response(resp.status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
        finally:
            upstream.close()

server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), LocalProxy)
worker = threading.Thread(target=server.serve_forever, daemon=True)
worker.start()
base = "http://127.0.0.1:" + str(server.server_address[1])
checks = {"unprivileged": os.geteuid() != 0, "host_sentinel_hidden": not pathlib.Path(outside).exists()}
try:
    with urllib.request.urlopen(base + "/api/version", timeout=15) as res:
        checks["metadata_via_tcp_to_unix"] = res.status == 200 and bool(json.loads(res.read()).get("version"))
except Exception:
    checks["metadata_via_tcp_to_unix"] = False

try:
    bad = {"model": "not-the-pinned-model", "messages": [{"role": "user", "content": "UNSAFE"}], "stream": False}
    request = urllib.request.Request(base + "/api/chat", data=json.dumps(bad).encode(),
                                     headers={"Content-Type":"application/json"})
    with urllib.request.urlopen(request, timeout=15) as res:
        checks["wrong_model_denied"] = False
except urllib.error.HTTPError as exc:
    checks["wrong_model_denied"] = exc.code == 403
except Exception:
    checks["wrong_model_denied"] = False

try:
    request = urllib.request.Request(base + "/api/delete", data=b"{}",
                                     headers={"Content-Type":"application/json"})
    with urllib.request.urlopen(request, timeout=15):
        checks["unsupported_endpoint_denied"] = False
except urllib.error.HTTPError as exc:
    checks["unsupported_endpoint_denied"] = exc.code == 403
except Exception:
    checks["unsupported_endpoint_denied"] = False

try:
    socket.create_connection(("1.1.1.1", 443), timeout=1).close()
    checks["outbound_tcp_denied"] = False
except OSError:
    checks["outbound_tcp_denied"] = True

try:
    pathlib.Path("/etc/ca-m0-escape").write_text("no")
    checks["root_fs_read_only"] = False
except OSError:
    checks["root_fs_read_only"] = True

try:
    body = json.dumps({"model": model, "messages": [
        {"role": "user", "content": "Reply with the single word READY."}], "stream": False}).encode()
    request = urllib.request.Request(base + "/api/chat", data=body,
                                     headers={"Content-Type":"application/json"})
    with urllib.request.urlopen(request, timeout=255) as res:
        answer = json.loads(res.read(262145))
        checks["inference_response_pinned"] = (
            res.status == 200 and answer.get("model") == model and answer.get("done") is True
            and bool(answer.get("message", {}).get("content", "").strip())
        )
        output_chars = len(answer.get("message", {}).get("content", ""))
except Exception as exc:
    checks["inference_response_pinned"] = False
    output_chars = 0

server.shutdown()
server.server_close()
print(json.dumps({"checks": checks, "assistant_content_characters": output_chars}, sort_keys=True))
"""

REQUIRED = frozenset((
    "unprivileged", "host_sentinel_hidden", "metadata_via_tcp_to_unix",
    "wrong_model_denied", "unsupported_endpoint_denied", "outbound_tcp_denied",
    "root_fs_read_only", "inference_response_pinned",
))


def assess(result: dict[str, Any]) -> bool:
    checks = result.get("checks")
    return isinstance(checks, dict) and set(checks) == REQUIRED and all(
        checks[key] is True for key in REQUIRED
    )


def command_for(image: str, workspace: pathlib.Path, bridge_dir: pathlib.Path,
                outside: pathlib.Path, model: str, uid: int, gid: int) -> list[str]:
    return [
        "podman", "run", "--rm", "--pull=never", "--network=none",
        "--read-only", "--cap-drop=all", "--security-opt=no-new-privileges",
        "--pids-limit=64", "--memory=512m", "--userns=keep-id",
        "--user", f"{uid}:{gid}",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=64m",
        "--volume", f"{workspace.resolve()}:/workspace:rw",
        "--volume", f"{bridge_dir.resolve()}:/run/codingassistant:ro",
        "--workdir=/workspace", image, "python3", "-c", CONTAINER_SCRIPT,
        "/run/codingassistant/ollama.sock", model, str(outside.resolve())
    ]


def run_probe(model: str, image: str, timeout: int) -> dict[str, Any]:
    report: dict[str, Any] = {
        "schema_version": 1, "recorded_at_utc": utcnow(), "model": model,
        "status": "BLOCKED", "purpose": "Pinned transport feasibility; no coding agent",
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        report["reason"] = "Linux, non-root Podman required"
        return report
    podman = inspect_podman(20)
    report["rootless"] = podman.get("rootless")
    if report["rootless"] is not True:
        report["reason"] = "Rootless Podman unavailable"
        return report
    image_info = image_status(image, 20)
    report["image_id"] = image_info.get("image_id")
    if not image_info.get("present"):
        report["reason"] = "Already-pulled image missing; no automatic pull"
        return report
    try:
        inventory = ollama_json("http://127.0.0.1:11434", "/api/tags")
        installed = next((item for item in inventory.get("models", [])
                          if item.get("name") == model and item.get("digest")), None)
        if not installed:
            raise ValueError("Exact pinned Ollama model and digest not available")
        digest = installed["digest"]
        report["model_digest"] = digest
    except (OSError, ValueError, KeyError) as exc:
        report["reason"] = f"Local Ollama pin failed: {type(exc).__name__}: {exc}"
        return report

    with tempfile.TemporaryDirectory(prefix="ca-m0-pinned-") as temp:
        root = pathlib.Path(temp)
        workspace = root / "work"
        workspace.mkdir(mode=0o700)
        socket_dir = root / "private"
        socket_dir.mkdir(mode=0o700)
        outside = root / "unmounted"
        outside.write_text("not mounted", encoding="utf-8")
        with serve_bridge(socket_dir / "ollama.sock", pinned_handler(model, digest)):
            command = command_for(image, workspace, socket_dir, outside, model,
                                  os.getuid(), os.getgid())
            try:
                p = subprocess.run(command, text=True, encoding="utf-8", errors="replace",
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   check=False, timeout=timeout)
            except (OSError, subprocess.TimeoutExpired) as exc:
                report["reason"] = f"Bounded probe failed to finish: {type(exc).__name__}: {exc}"
                return report
        report["container_exit_code"] = p.returncode
        if p.returncode != 0:
            report["reason"] = "Container exited nonzero"
            report["stderr_tail"] = p.stderr[-500:]
            return report
        try:
            result = json.loads(p.stdout.strip())
        except ValueError:
            report["reason"] = "Container output invalid"
            return report
        report["checks"] = result.get("checks")
        report["output_characters"] = result.get("assistant_content_characters")
        if not assess(result):
            report["reason"] = "At least one required security or inference check failed"
            return report
        report["status"] = "INFERENCE_PASS"
        return report


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--model", default="qwen3.5:4b")
    ap.add_argument("--image", default=DEFAULT_IMAGE)
    ap.add_argument("--timeout", type=int, default=360)
    ap.add_argument("--output", type=pathlib.Path,
                    default=ROOT / "artifacts/m0/inference-transport.json")
    args = ap.parse_args()
    if args.timeout < 60 or args.timeout > 900:
        ap.error("Timeout must be 60–900 seconds")
    if not args.model.strip() or "/" in args.model:
        ap.error("Model must be an exact local Ollama tag")
    result = run_probe(args.model, args.image, args.timeout)
    write_json(args.output, result)
    print(f'M0.6 ISOLATED INFERENCE {result["status"]}: {args.model}')
    for key, good in sorted((result.get("checks") or {}).items()):
        print(f'  {key}: {"PASS" if good else "FAIL"}')
    if result.get("reason"):
        print("Reason:", result["reason"])
    print("Evidence:", args.output)
    print("No AI-generated code or shell command was executed.")
    return 0 if result["status"] == "INFERENCE_PASS" else 2


if __name__ == "__main__":
    sys.exit(main())
