#!/usr/bin/env python3
"""M0.8b-2b: real pinned Qwen answer through isolated OpenCode (NO tools).

Uses existing immutable local M0.7 image, non-root Podman --network=none,
private Unix socket, one fixed inference prompt and zero worktree mounts.
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
from pinned_inference import verify_model_digest
from pinned_real_text import pinned_real_text_handler
from runtime_image_probe import IMAGE
from sandbox_probe import image_status, inspect_podman

SCRIPT = pathlib.Path(__file__).with_name("isolated_opencode_real.mjs")
REQUIRED = frozenset({
    "unprivileged", "no_new_privileges", "no_effective_capabilities",
    "root_read_only", "host_sentinel_hidden", "outbound_tcp_denied",
    "metadata_reached", "model_profile_verified", "wrong_show_model_denied",
    "cli_exited_successfully", "one_inference_request", "ready_output",
    "unsupported_routes_denied", "no_unexpected_cli_route",
})


def command_for(image_id: str, uid: int, gid: int, private: pathlib.Path,
                model: str, sentinel: pathlib.Path) -> list[str]:
    if not re.fullmatch(r"(?:sha256:)?[a-fA-F0-9]{64}", image_id):
        raise ValueError("Require full immutable existing local image ID")
    payload(model)
    if not private.is_dir() or not sentinel.is_file():
        raise ValueError("Disposable private socket and sentinel required")
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


def checks_pass(report: dict[str, Any]) -> bool:
    checks = report.get("checks")
    return (type(checks) is dict and set(checks) == REQUIRED
            and all(checks.get(key) is True for key in REQUIRED))


def probe(model: str, timeout: int) -> dict[str, Any]:
    report: dict[str, Any] = {
        "schema_version": 1, "stage": "M0.8b-2b-real",
        "recorded_at_utc": utcnow(), "model": model, "status": "BLOCKED",
        "purpose": "one real model text answer, no generated tool or code execution"
    }
    if not sys.platform.startswith("linux") or os.geteuid() == 0:
        report["reason"] = "Requires Linux rootless Podman"
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
        report["reason"] = "Already verified offline image is missing"
        return report
    report["image_id"] = image_id
    try:
        inventory = ollama_json("http://127.0.0.1:11434", "/api/tags")
        matches = [entry for entry in inventory.get("models", [])
                   if entry.get("name") == model and entry.get("digest")]
        if len(matches) != 1:
            raise ValueError("Exact model/digest not installed")
        digest = matches[0]["digest"]
        if not verify_model_digest(model, digest):
            raise ValueError("Model digest drift before test")
        report["model_digest"] = digest
    except (ValueError, OSError, RuntimeError, KeyError, TypeError) as exc:
        report["reason"] = f"Local model pin failed: {type(exc).__name__}"
        return report

    state = {"attempts": 0, "completed": 0}
    with tempfile.TemporaryDirectory(prefix="ca-m08b-real-") as temp:
        private = pathlib.Path(temp) / "private"
        private.mkdir(mode=0o700)
        sentinel = pathlib.Path(temp) / "host-file-not-mounted"
        sentinel.write_text("cannot see this on guest", encoding="utf-8")
        with serve_bridge(private / "ollama.sock",
                          pinned_real_text_handler(model, digest, state)):
            try:
                cmd = command_for(image_id, os.getuid(), os.getgid(),
                                  private, model, sentinel)
                run = subprocess.run(
                    cmd, stdin=subprocess.DEVNULL, check=False,
                    stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                    text=True, encoding="utf-8", errors="replace", timeout=timeout)
            except (subprocess.TimeoutExpired, OSError, ValueError) as exc:
                report["reason"] = f"Isolated OpenCode run failed: {type(exc).__name__}"
                return report

    report["container_exit_code"] = run.returncode
    report["provider_requests"] = state["attempts"]
    report["provider_completed"] = state["completed"]
    try:
        result = json.loads(run.stdout.strip())
        report["checks"] = result.get("checks", {})
        routes = result.get("observed_routes")
        if isinstance(routes, list):
            report["observed_routes"] = [str(route)[:96] for route in routes[:20]]
    except (ValueError, AttributeError, TypeError):
        report["reason"] = "No valid bounded container checks returned"
        report["stderr_tail"] = run.stderr[-300:]
        return report
    if not verify_model_digest(model, digest):
        report["reason"] = "Model digest changed after inference"
        return report
    if (run.returncode != 0 or not checks_pass(report)
            or state != {"attempts": 1, "completed": 1}):
        report["reason"] = "Isolated OpenCode model inference or security checks failed"
        report["stderr_tail"] = run.stderr[-300:]
        return report
    report["status"] = "REAL_INFERENCE_PASS"
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default="qwen3.5:4b")
    parser.add_argument("--timeout", type=int, default=420)
    parser.add_argument("--output", type=pathlib.Path,
                        default=ROOT / "artifacts/m0/opencode-real-inference.json")
    args = parser.parse_args()
    if not 300 <= args.timeout <= 900:
        parser.error("Timeout must be between 300 and 900 seconds")
    result = probe(args.model, args.timeout)
    write_json(args.output, result)
    print(f'M0.8b-2b REAL ISOLATED OPENCODE INFERENCE {result["status"]}: {args.model}')
    for key, value in sorted((result.get("checks") or {}).items()):
        print(f'  {key}: {"PASS" if value else "FAIL"}')
    if "container_exit_code" in result:
        print("Container exit code:", result["container_exit_code"])
    if "provider_requests" in result:
        print("Pinned model requests:", result["provider_requests"],
              "completed:", result["provider_completed"])
    if result.get("observed_routes"):
        print("Observed routes:", ", ".join(result["observed_routes"]))
    if "reason" in result:
        print("Reason:", result["reason"])
    if "stderr_tail" in result:
        print("Container error (bounded):", result["stderr_tail"])
    print("Evidence:", args.output)
    print("Model text was displayed only. No tool, generated code or repository was executed.")
    return 0 if result["status"] == "REAL_INFERENCE_PASS" else 2


if __name__ == "__main__":
    sys.exit(main())
