#!/usr/bin/env python3
"""M1.0 read-only Gateway/ACP/OpenCode host compatibility inventory.

Does NOT start a Gateway, install packages, read OpenClaw configuration,
contact inference endpoints, invoke an ACP session, or run an agent.
Only locally installed --version commands are invoked, with bounded time.
Results omit executable paths and arbitrary command output.
"""
from __future__ import annotations

import datetime as dt
import json
import os
import pathlib
import platform
import re
import shutil
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "artifacts/m1/gateway-discovery.json"
VERSION = re.compile(r"(?<![\w.])v?(\d+)\.(\d+)\.(\d+)(?:[-+][\w.]+)?(?![\w.])")


def parse_semver(value: str | None) -> tuple[int, int, int] | None:
    match = VERSION.search(value or "")
    return tuple(map(int, match.groups())) if match else None


def node_compat(version: str | None) -> dict[str, bool]:
    parsed = parse_semver(version)
    # Node's supported 24/26 stable releases explicitly exclude prerelease
    # versions even if their numeric components meet the production floor.
    if parsed is None or re.search(r"v?\\d+\\.\\d+\\.\\d+-", version or ""):
        return {"supported": False, "patched_recommended": False}
    major, minor, patch = parsed
    supported = ((major == 24 and (minor, patch) >= (16, 0)) or
                 (major == 26 and (minor, patch) >= (1, 0)))
    patched = ((major == 24 and (minor, patch) >= (22, 1)) or
               (major == 26 and (minor, patch) >= (12, 1)))
    # Node 27 is a development release, not our initial supported Gateway
    # deployment. An explicit later architecture gate may evaluate it.
    return {"supported": supported, "patched_recommended": patched}


def local_version(program: str, timeout: int = 8) -> dict:
    """No shell expansion, arbitrary arguments, or access to CLI config APIs."""
    executable = shutil.which(program)
    if not executable:
        return {"installed": False, "version": None, "exit_code": None}
    # No credentials/provider tokens forwarded to subprocesses. PATH and HOME
    # are retained only to resolve the already-installed executable/runtime.
    env = {key: value for key, value in os.environ.items()
           if key in {"PATH", "HOME", "USER", "LOGNAME", "LANG", "LC_ALL",
                      "XDG_CONFIG_HOME", "XDG_DATA_HOME", "SYSTEMROOT",
                      "WINDIR", "APPDATA", "LOCALAPPDATA"}}
    env["NO_COLOR"] = "1"
    try:
        completed = subprocess.run(
            [executable, "--version"], stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace",
            env=env, timeout=timeout, check=False)
        # Only a version token is persisted. Never store paths, config,
        # environment variables, generic stdout, or long error traces.
        output = (completed.stdout + "\n" + completed.stderr)[:1000]
        match = VERSION.search(output)
        return {"installed": True,
                "version": match.group(0) if match else None,
                "exit_code": completed.returncode}
    except (OSError, subprocess.TimeoutExpired):
        return {"installed": True, "version": None, "exit_code": None}


def discover() -> dict:
    tools = {name: local_version(name)
             for name in ("node", "openclaw", "opencode", "podman")}
    node = node_compat(tools["node"]["version"])
    prerequisites = {
        "node_supported": node["supported"],
        "node_patched_recommended": node["patched_recommended"],
        "openclaw_available": (
            tools["openclaw"]["installed"] and
            tools["openclaw"]["exit_code"] == 0 and
            tools["openclaw"]["version"] is not None),
        "podman_available": (
            tools["podman"]["installed"] and
            tools["podman"]["exit_code"] == 0),
    }
    # OpenCode is embedded in the M0 verified container; a host executable
    # is informational only, NOT an M1 readiness prerequisite.
    # acpx is an OpenClaw EMBEDDED plugin; no external acpx binary required.
    # This does not inspect user config or assume the plugin is installed.
    eligible = (prerequisites["node_supported"] and
                prerequisites["openclaw_available"] and
                prerequisites["podman_available"])
    return {
        "schema_version": 1,
        "stage": "M1.0-gateway-prerequisites",
        "status": "DISCOVERY_READY" if eligible else "PREREQUISITES_BLOCKED",
        "recorded_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "platform": {"system": platform.system(), "machine": platform.machine()},
        "tools": tools,
        "prerequisites": prerequisites,
        "acpx_runtime": "EMBEDDED_PLUGIN_NOT_INSPECTED",
        "gateway_connection": "NOT_ATTEMPTED",
        "acp_session": "NOT_ATTEMPTED",
        "model_inference": "NOT_ATTEMPTED",
        "source_changes": "NONE",
        "note": ("Discovery only; M1 Gateway/ACP/child-process security "
                 "and Linux/Windows acceptance gates remain unverified."),
    }


def main() -> int:
    result = discover()
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(json.dumps(result, sort_keys=True, indent=2) + "\n",
                      encoding="utf-8")
    print("M1.0", result["status"])
    for name in ("node", "openclaw", "opencode", "podman"):
        info = result["tools"][name]
        print(f"  {name}: {info['version'] or 'NOT_AVAILABLE'}")
    for name, passed in result["prerequisites"].items():
        print(f"  {name}: {'YES' if passed else 'NO'}")
    print("  acpx: EMBEDDED_PLUGIN_NOT_INSPECTED")
    print("  Gateway/ACP/agent execution: NOT_ATTEMPTED")
    print("Evidence:", OUTPUT)
    return 0 if result["status"] == "DISCOVERY_READY" else 2


if __name__ == "__main__":
    sys.exit(main())
