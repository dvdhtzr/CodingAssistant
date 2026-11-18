#!/usr/bin/env python3
"""M0.8b-1: fixed OpenAI tool stream through private AF_UNIX to isolated Node.

No OpenCode session, generated code, tool execution or project source mounts.
The pinned host broker can only call local Ollama with one literal request.
"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
from typing import Any

from common import ROOT, ollama_json, utcnow, write_json
from ollama_bridge import serve_bridge
from openai_wire_probe import payload
from pinned_openai import pinned_openai_handler
from pinned_inference import verify_model_digest
from sandbox_probe import image_status, inspect_podman
from runtime_image_probe import IMAGE

SCRIPT = pathlib.Path(__file__).with_name("isolated_openai_client.mjs")
REQUIRED = frozenset({
    "unprivileged", "root_read_only", "host_sentinel_hidden",
    "outbound_tcp_denied", "metadata_pinned", "wrong_model_denied",
    "unsupported_path_denied", "sse_tool_call"
})


def run_command(image_id: str, uid: int, gid: int, bridge_dir: pathlib.Path,
                model: str, sentinel: pathlib.Path) -> list[str]:
    if not re.fullmatch(r"(?:sha256:)?[a-fA-F0-9]{64}", image_id):
        raise ValueError("Require a full immutable local image ID")
    if not bridge_dir.is_dir() or not sentinel.is_file():
        raise ValueError("Expected disposable isolation fixtures")
    source = SCRIPT.read_text(encoding="utf-8")
    return [
        "podman", "run", "--rm", "--pull=never",
        "--network=none", "--read-only", "--cap-drop=all",
        "--security-opt=no-new-privileges", "--pids-limit=96",
        "--memory=2g", "--userns=keep-id", "--user", f"{uid}:{gid}",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup",
        "--volume", f"{bridge_dir.resolve()}:/run/codingassistant:ro",
        "--env", "HOME=/tmp", "--env", "XDG_CONFIG_HOME=/tmp/.config",
        "--env", "XDG_DATA_HOME=/tmp/.local/share",
        "--workdir=/tmp", "--entrypoint=node", image_id,
        "--input-type=module", "--eval", source, "--", model,
        json.dumps(payload(model), separators=(",", ":")),
        str(sentinel.resolve())
    ]


def assess(value: dict[str, Any]) -> bool:
    checks = value.get("checks")
    return (isinstance(checks, dict) and set(checks) == REQUIRED
            and all(checks[key] is True for key in REQUIRED))


def probe(model: str, timeout: int) -> dict[str, Any]:
    report: dict[str, Any] = {
        "schema_version": 1, "stage": "M0.8b-1", "recorded_at_utc": utcnow(),
        "status": "BLOCKED", "model": model,
        "purpose": "fixed private OpenAI wire; no agent, worktree or code execution"
    }
    if not sys.platform.startswith("linux") or os.geteuid() == 0:
        report["reason"] = "Linux non-root Podman is required"
        return report
    try:
        payload(model)
    except ValueError as exc:
        report["reason"] = str(exc)
        return report
    podman = inspect_podman(20)
    if podman.get("rootless") is not True:
        report["reason"] = "Rootless Podman unavailable"
        return report
    image = image_status(IMAGE, 20)
    if not image.get("present") or not image.get("image_id"):
        report["reason"] = "Existing M0.7 offline image missing"
        return report
    report["image_id"] = image["image_id"]
    try:
        inventory = ollama_json("http://127.0.0.1:11434", "/api/tags")
        installed = [item for item in inventory.get("models", [])
                     if item.get("name") == model and item.get("digest")]
        if len(installed) != 1:
            raise ValueError("Exact pinned model missing")
        digest = installed[0]["digest"]
        if not verify_model_digest(model, digest):
            raise ValueError("Pinned model changed before probing")
        report["model_digest"] = digest
    except (OSError, RuntimeError, ValueError, KeyError) as exc:
        report["reason"] = f"Local model pin failed: {type(exc).__name__}"
        return report
    with tempfile.TemporaryDirectory(prefix="ca-m08b-") as parent:
        path = pathlib.Path(parent)
        private = path / "private"
        private.mkdir(mode=0o700)
        sentinel = path / "unmounted-host-sentinel"
        sentinel.write_text("no host mount", encoding="utf-8")
        with serve_bridge(private / "ollama.sock", pinned_openai_handler(model, digest)):
            try:
                cmd = run_command(image["image_id"], os.getuid(), os.getgid(),
                                  private, model, sentinel)
                run = subprocess.run(cmd, stdin=subprocess.DEVNULL, check=False,
                                     stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                     text=True, encoding="utf-8", errors="replace",
                                     timeout=timeout)
            except (OSError, subprocess.TimeoutExpired, ValueError) as exc:
                report["reason"] = f"Isolated wire probe failed: {type(exc).__name__}"
                return report
        report["container_exit_code"] = run.returncode
        if run.returncode != 0:
            report["reason"] = "Isolated Node probe failed or was denied"
            report["stderr_tail"] = run.stderr[-500:]
        try:
            data = json.loads(run.stdout.strip())
            report["checks"] = data.get("checks", {})
        except ValueError:
            report["reason"] = "Container produced no valid fixed-check JSON"
            return report
    if not assess(report) or report["container_exit_code"] != 0:
        report.setdefault("reason", "Required isolation or pinned inference check failed")
        return report
    if not verify_model_digest(model, digest):
        report["reason"] = "Pinned model digest changed after inference"
        return report
    report["status"] = "ISOLATED_WIRE_PASS"
    return report


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--model", default="qwen3.5:4b")
    p.add_argument("--timeout", type=int, default=420)
    p.add_argument("--output", type=pathlib.Path,
                   default=ROOT / "artifacts/m0/isolated-openai-wire.json")
    args = p.parse_args()
    if not 120 <= args.timeout <= 900:
        p.error("Timeout must be 120–900 seconds")
    result = probe(args.model, args.timeout)
    write_json(args.output, result)
    print(f'M0.8b-1 ISOLATED OPENAI WIRE {result["status"]}: {args.model}')
    for key, good in sorted((result.get("checks") or {}).items()):
        print(f'  {key}: {"PASS" if good else "FAIL"}')
    if result.get("reason"):
        print("Reason:", result["reason"])
    if "container_exit_code" in result:
        print("Container exit code:", result["container_exit_code"])
    if result.get("stderr_tail"):
        print("Container error (bounded):", result["stderr_tail"])
    print("Evidence:", args.output)
    print("No OpenCode session, model-suggested tool, or generated code executed.")
    return 0 if result["status"] == "ISOLATED_WIRE_PASS" else 2


if __name__ == "__main__":
    sys.exit(main())
