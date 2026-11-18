#!/usr/bin/env python3
"""M0.7: build and inspect a local OpenCode/.NET/Node toolchain image safely.

No model calls, source mounts, OpenCode sessions, or generated-code execution.
The --build flag explicitly allows downloads *during the image build*.
Every runtime check uses rootless Podman with --network=none.
"""
from __future__ import annotations

import argparse
import os
import pathlib
import re
import subprocess
import sys
from typing import Any

from common import ROOT, utcnow, write_json
from sandbox_probe import image_status, inspect_podman

CONTEXT = ROOT / "infra/runtime"
IMAGE = "localhost/codingassistant-m0-runtime:2.3.0"
VERSION_PIN = "2.3.0"

# The model is never invoked; only tool version and CLI-help commands execute.
VERSION_SCRIPT = """set -eu
printf 'dotnet='; dotnet --version
printf 'node='; node --version
printf 'npm='; npm --version
printf 'git='; git --version
printf 'opencode='; opencode --version
opencode run --help > /dev/null
printf 'run_help=ok\\n'
"""

EXPECTED = frozenset({"dotnet", "node", "npm", "git", "opencode", "run_help"})


def build_command() -> list[str]:
    # Only this tiny context is sent to the local rootless builder.
    return ["podman", "build", "--pull=always", "--file", str(CONTEXT / "Containerfile"),
            "--tag", IMAGE, str(CONTEXT)]


def run_command(image_id: str, uid: int, gid: int) -> list[str]:
    # Podman image inspect --format '{{.Id}}' normally returns the bare
    # 64-hex immutable image ID. Some engines add a 'sha256:' prefix.
    # Both forms identify a specific local image; mutable tags and short IDs
    # remain forbidden.
    if not re.fullmatch(r"(?:sha256:)?[a-fA-F0-9]{64}", image_id):
        raise ValueError("Require a full 64-hex local image ID, not a mutable tag")
    return [
        "podman", "run", "--rm", "--pull=never",
        "--network=none", "--read-only", "--cap-drop=all",
        "--security-opt=no-new-privileges", "--pids-limit=96",
        "--memory=2g", "--userns=keep-id", "--user", f"{uid}:{gid}",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=512m",
        "--env", "HOME=/tmp",
        "--env", "DOTNET_CLI_HOME=/tmp",
        "--env", "XDG_CONFIG_HOME=/tmp/.config",
        "--env", "XDG_DATA_HOME=/tmp/.local/share",
        "--env", "XDG_STATE_HOME=/tmp/.local/state",
        "--env", "XDG_CACHE_HOME=/tmp/.cache",
        "--env", "OPENCODE_DISABLE_AUTOUPDATE=1",
        "--workdir=/workspace",
        "--entrypoint=/bin/sh", image_id, "-c", VERSION_SCRIPT
    ]


def parse_versions(stdout: str) -> dict[str, str]:
    result: dict[str, str] = {}
    for line in stdout.splitlines():
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        if key in EXPECTED and key not in result:
            result[key] = value.strip()
    return result


def check_versions(v: dict[str, str]) -> dict[str, bool]:
    return {
        "dotnet_sdk_10": bool(re.fullmatch(r"10\.\d+\.\d+(?:[-+][\w.-]+)?", v.get("dotnet", ""))),
        "node_22": bool(re.fullmatch(r"v22\.\d+\.\d+", v.get("node", ""))),
        "npm_available": bool(re.fullmatch(r"\d+\.\d+\.\d+", v.get("npm", ""))),
        "git_available": v.get("git", "").startswith("git version "),
        "opencode_pinned": bool(re.fullmatch(r"(?:opencode v)?" + re.escape(VERSION_PIN), v.get("opencode", ""))),
        "opencode_run_help": v.get("run_help") == "ok"
    }


def evaluate(build: bool, timeout: int) -> dict[str, Any]:
    result: dict[str, Any] = {
        "schema_version": 1, "recorded_at_utc": utcnow(),
        "status": "BLOCKED", "stage": "M0.7", "image": IMAGE,
        "opencode_version_pin": VERSION_PIN,
        "purpose": "toolchain startup only; no agent and no model inference"
    }
    if not sys.platform.startswith("linux") or os.geteuid() == 0:
        result["reason"] = "Run on Linux as a normal user; never sudo"
        return result
    rootless = inspect_podman(min(20, timeout))
    result["podman_rootless"] = rootless.get("rootless")
    if not result["podman_rootless"]:
        result["reason"] = "Rootless Podman is unavailable"
        return result
    if build:
        log = ROOT / "artifacts/m0/runtime-image-build.log"
        log.parent.mkdir(parents=True, exist_ok=True)
        result["build_log"] = str(log)
        try:
            with log.open("wb") as writer:
                p = subprocess.run(build_command(), stdout=writer, stderr=subprocess.STDOUT,
                                   stdin=subprocess.DEVNULL, check=False, timeout=timeout)
            result["build_exit_code"] = p.returncode
            if p.returncode != 0:
                result["reason"] = "Image build failed; inspect ignored build log"
                return result
        except (OSError, subprocess.TimeoutExpired) as exc:
            result["reason"] = f"Image build interrupted: {type(exc).__name__}: {exc}"
            return result
    info = image_status(IMAGE, min(20, timeout))
    if not info.get("present"):
        result["reason"] = "Image missing; explicitly pass --build to fetch its dependencies"
        return result
    image_id = info.get("image_id") or ""
    result["image_id"] = image_id
    try:
        command = run_command(image_id, os.getuid(), os.getgid())
    except ValueError as exc:
        result["reason"] = str(exc)
        return result
    try:
        p = subprocess.run(command, stdin=subprocess.DEVNULL,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False,
                           text=True, encoding="utf-8", errors="replace", timeout=min(timeout, 180))
    except (OSError, subprocess.TimeoutExpired) as exc:
        result["reason"] = f"Offline toolchain verification failed: {type(exc).__name__}: {exc}"
        return result
    result["run_exit_code"] = p.returncode
    versions = parse_versions(p.stdout)
    result["versions"] = versions
    checks = check_versions(versions)
    result["checks"] = checks
    if p.returncode != 0 or not all(checks.values()):
        # These commands only print CLI versions/help. Never dump environments,
        # mounted files, full logs, credentials or command line arguments.
        # A Podman launch failure has no version output: do not misreport
        # six absent development tools when the process never started.
        if p.returncode != 0 and not versions:
            result["reason"] = f"Podman could not start the offline toolchain (exit {p.returncode})"
        elif p.returncode != 0:
            result["reason"] = f"Offline toolchain script failed partway through (exit {p.returncode})"
        else:
            result["reason"] = "Version output incomplete or different from expected toolchain"
        result["stderr_tail"] = p.stderr[-1200:]
        result["stdout_tail"] = p.stdout[-1200:]
        return result
    result["status"] = "TOOLCHAIN_PASS"
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build", action="store_true", help="Explicitly fetch images/packages during rootless build")
    parser.add_argument("--timeout", type=int, default=1800)
    parser.add_argument("--output", type=pathlib.Path,
                        default=ROOT / "artifacts/m0/runtime-image.json")
    args = parser.parse_args()
    if not 180 <= args.timeout <= 3600:
        parser.error("Timeout must be between 180 and 3600 seconds")
    result = evaluate(args.build, args.timeout)
    write_json(args.output, result)
    print(f'M0.7 OFFLINE TOOLCHAIN {result["status"]}')
    for key, ok in (result.get("checks") or {}).items():
        print(f'  {key}: {"PASS" if ok else "FAIL"}')
    if result.get("reason"):
        print("Reason:", result["reason"])
    if "run_exit_code" in result:
        print("Container exit code:", result["run_exit_code"])
    if result.get("stderr_tail"):
        print("Container error output (last 1200 characters):")
        print(result["stderr_tail"])
    if result.get("stdout_tail"):
        print("Container output (last 1200 characters):")
        print(result["stdout_tail"])
    if result.get("build_log"):
        print("Build log:", result["build_log"])
    print("Evidence:", args.output)
    print("No autonomous coding agent, model call or generated code executed.")
    return 0 if result["status"] == "TOOLCHAIN_PASS" else 2


if __name__ == "__main__":
    sys.exit(main())
