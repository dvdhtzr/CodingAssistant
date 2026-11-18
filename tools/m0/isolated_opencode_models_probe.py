#!/usr/bin/env python3
"""M0.8b-2a: exercise OpenCode's read-only models CLI in isolated Podman.

No inference prompts, autonomous agent runs, generated tool execution or
repository mounts. Unsupported model metadata endpoints are denied and logged
as bounded route labels to guide the subsequent compatibility implementation.
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
from runtime_image_probe import IMAGE
from sandbox_probe import image_status, inspect_podman

SCRIPT = pathlib.Path(__file__).with_name("isolated_opencode_models.mjs")
REQUIRED = frozenset({
    "unprivileged", "no_new_privileges", "no_effective_capabilities",
    "root_read_only", "host_sentinel_hidden", "outbound_tcp_denied",
    "metadata_reached", "cli_exited_successfully", "exact_model_visible",
    "only_local_provider", "unsupported_routes_denied",
})


def command_for(image_id: str, uid: int, gid: int, private: pathlib.Path,
                model: str, sentinel: pathlib.Path) -> list[str]:
    if not re.fullmatch(r"(?:sha256:)?[a-fA-F0-9]{64}", image_id):
        raise ValueError("Require immutable 64-character local image ID")
    payload(model)  # Exact local Ollama tag; no untrusted request parameters.
    if not private.is_dir() or not sentinel.is_file():
        raise ValueError("Disposable isolation fixtures missing")
    return [
        "podman", "run", "--rm", "--pull=never",
        "--network=none", "--read-only", "--cap-drop=all",
        "--security-opt=no-new-privileges", "--pids-limit=96",
        "--memory=2g", "--userns=keep-id", "--user", f"{uid}:{gid}",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup",
        "--volume", f"{private.resolve()}:/run/codingassistant:ro",
        "--env", "HOME=/tmp", "--env", "XDG_CONFIG_HOME=/tmp/.config",
        "--env", "XDG_DATA_HOME=/tmp/.local/share",
        "--env", "OPENCODE_DISABLE_AUTOUPDATE=1",
        "--workdir=/tmp", "--entrypoint=node", image_id,
        "--input-type=module", "--eval", SCRIPT.read_text(encoding="utf-8"),
        "--", model, str(sentinel.resolve())
    ]


def checks_pass(data: dict[str, Any]) -> bool:
    checks = data.get("checks")
    return (isinstance(checks, dict) and set(checks) == REQUIRED
            and all(checks[key] is True for key in REQUIRED))


def probe(model: str, timeout: int) -> dict[str, Any]:
    report: dict[str, Any] = {
        "schema_version": 1, "stage": "M0.8b-2a",
        "recorded_at_utc": utcnow(), "model": model, "status": "BLOCKED",
        "purpose": "OpenCode models discovery only; never opencode run",
    }
    if not sys.platform.startswith("linux") or os.geteuid() == 0:
        report["reason"] = "Linux rootless Podman required"
        return report
    try:
        payload(model)
    except ValueError as exc:
        report["reason"] = str(exc)
        return report
    if inspect_podman(20).get("rootless") is not True:
        report["reason"] = "Rootless Podman unavailable"
        return report
    image = image_status(IMAGE, 20)
    image_id = image.get("image_id")
    if not image.get("present") or not image_id:
        report["reason"] = "Existing M0.7 runtime image missing"
        return report
    report["image_id"] = image_id
    try:
        inventory = ollama_json("http://127.0.0.1:11434", "/api/tags")
        matches = [entry for entry in inventory.get("models", [])
                   if entry.get("name") == model and entry.get("digest")]
        if len(matches) != 1:
            raise ValueError("Exact local pinned model unavailable")
        digest = matches[0]["digest"]
        if not verify_model_digest(model, digest):
            raise ValueError("Local model digest changed")
        report["model_digest"] = digest
    except (OSError, ValueError, RuntimeError, KeyError) as exc:
        report["reason"] = f"Local model pin unavailable: {type(exc).__name__}"
        return report

    with tempfile.TemporaryDirectory(prefix="ca-m08b2-models-") as directory:
        root = pathlib.Path(directory)
        private = root / "private"
        private.mkdir(mode=0o700)
        sentinel = root / "host-sentinel-not-mounted"
        sentinel.write_text("private host data", encoding="utf-8")
        with serve_bridge(private / "ollama.sock", pinned_openai_handler(model, digest)):
            try:
                cmd = command_for(image_id, os.getuid(), os.getgid(),
                                  private, model, sentinel)
                completed = subprocess.run(cmd, stdin=subprocess.DEVNULL,
                                           stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                           text=True, encoding="utf-8", errors="replace",
                                           check=False, timeout=timeout)
            except (OSError, ValueError, subprocess.TimeoutExpired) as exc:
                report["reason"] = f"Read-only OpenCode discovery did not complete: {type(exc).__name__}"
                return report
        report["container_exit_code"] = completed.returncode
        try:
            result = json.loads(completed.stdout.strip())
        except ValueError:
            report["reason"] = "Container produced no valid summary JSON"
            report["stderr_tail"] = completed.stderr[-300:]
            return report
        report["checks"] = result.get("checks", {})
        routes = result.get("observed_routes")
        if isinstance(routes, list):
            report["observed_routes"] = [str(x)[:96] for x in routes[:16]]
    if not verify_model_digest(model, digest):
        report["reason"] = "Pinned model digest changed after discovery"
        return report
    if not checks_pass(report) or report["container_exit_code"] != 0:
        report["reason"] = "OpenCode isolated discovery or protocol check failed"
        report["stderr_tail"] = completed.stderr[-300:]
        return report
    report["status"] = "DISCOVERY_PASS"
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default="qwen3.5:4b")
    parser.add_argument("--timeout", type=int, default=120)
    parser.add_argument("--output", type=pathlib.Path,
                        default=ROOT / "artifacts/m0/opencode-isolated-models.json")
    args = parser.parse_args()
    if not 90 <= args.timeout <= 300:
        parser.error("Timeout must be 90-300 seconds")
    report = probe(args.model, args.timeout)
    write_json(args.output, report)
    print(f'M0.8b-2a ISOLATED OPENCODE DISCOVERY {report["status"]}: {args.model}')
    for key, value in sorted((report.get("checks") or {}).items()):
        print(f'  {key}: {"PASS" if value else "FAIL"}')
    if "container_exit_code" in report:
        print("Container exit code:", report["container_exit_code"])
    if report.get("observed_routes"):
        print("Requested metadata routes:", ", ".join(report["observed_routes"]))
    if report.get("reason"):
        print("Reason:", report["reason"])
    if report.get("stderr_tail"):
        print("Container error (bounded):", report["stderr_tail"])
    print("Evidence:", args.output)
    print("No OpenCode prompt, inference, generated code or agent-directed tool was executed.")
    return 0 if report["status"] == "DISCOVERY_PASS" else 2


if __name__ == "__main__":
    sys.exit(main())
