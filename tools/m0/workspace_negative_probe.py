#!/usr/bin/env python3
"""M0.10: deterministic disposable-workspace negative tests, with no agent.

Tests denylisted filesystem, symlink, child, network and Git behaviors in an
OS-restricted Podman container. Uses only a NEW copy of the frozen C# fixture.
No OpenCode, Ollama or model-generated code runs. Not M1 certification.
"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
from typing import Any

from common import ROOT, utcnow, write_json
from disposable_workspace_probe import SOURCE, command_for, fixture_hash
from runtime_image_probe import IMAGE
from sandbox_probe import image_status, inspect_podman

CLIENT = pathlib.Path(__file__).with_name("workspace_attack_client.mjs")
REQUIRED = frozenset((
    "non_root_identity", "no_effective_capabilities",
    "no_new_privileges", "root_mount_read_only",
    "host_sentinel_not_visible", "root_config_write_denied",
    "relative_traversal_write_denied", "symlink_outside_write_denied",
    "symlink_host_read_denied", "disposable_workspace_write_available",
    "unrelated_git_repository_absent", "private_broker_socket_absent",
    "child_identity_and_privileges_held", "child_external_tcp_denied",
    "child_host_sentinel_not_visible", "git_remote_connection_denied",
    "original_fixture_unchanged", "disposable_copy_restored",
))


def attack_command(image_id: str, uid: int, gid: int,
                   workspace: pathlib.Path, sentinel: pathlib.Path) -> list[str]:
    """Keep the proven M0.9 mount/security contract, swap ONLY fixed script."""
    cmd = command_for(image_id, uid, gid, workspace, sentinel)
    position = cmd.index("--eval") + 1
    cmd[position] = CLIENT.read_text(encoding="utf-8")
    return cmd


def checks_pass(report: dict[str, Any]) -> bool:
    checks = report.get("checks")
    return (type(checks) is dict and set(checks) == REQUIRED
            and all(checks.get(key) is True for key in REQUIRED))


def probe(timeout: int) -> dict[str, Any]:
    report: dict[str, Any] = {
        "schema_version": 1, "stage": "M0.10",
        "recorded_at_utc": utcnow(), "status": "BLOCKED",
        "purpose": "disposable OS sandbox negative tests, no agent execution",
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        report["reason"] = "Rootless Linux Podman required"
        return report
    if inspect_podman(20).get("rootless") is not True:
        report["reason"] = "Podman does not report rootless operation"
        return report
    image = image_status(IMAGE, 20)
    if not image.get("present") or not image.get("image_id"):
        report["reason"] = "Already-approved local toolchain image missing"
        return report
    report["image_id"] = image["image_id"]
    try:
        baseline = fixture_hash(SOURCE)
    except (ValueError, OSError) as exc:
        report["reason"] = f"Frozen fixture integrity failure: {type(exc).__name__}"
        return report
    report["fixture_sha256"] = baseline
    with tempfile.TemporaryDirectory(prefix="ca-m010-negative-") as temp:
        parent = pathlib.Path(temp)
        workspace = parent / "workspace"
        sentinel = parent / "outside-sentinel"
        sentinel.write_text("ONLY VISIBLE TO HOST", encoding="utf-8")
        shutil.copytree(SOURCE, workspace)
        try:
            if fixture_hash(workspace) != baseline:
                raise ValueError("Workspace starts with wrong source hash")
            cmd = attack_command(image["image_id"], os.getuid(), os.getgid(),
                                 workspace, sentinel)
            run = subprocess.run(
                cmd, stdin=subprocess.DEVNULL, capture_output=True, text=True,
                encoding="utf-8", errors="replace", timeout=timeout, check=False)
        except (OSError, ValueError, subprocess.TimeoutExpired) as exc:
            report["reason"] = f"Isolated negative tests failed to start: {type(exc).__name__}"
            return report
        report["container_exit_code"] = run.returncode
        try:
            guest = json.loads(run.stdout.strip())
            if type(guest) is not dict or type(guest.get("checks")) is not dict:
                raise ValueError("Guest negative checks absent")
            report["checks"] = guest["checks"]
        except (ValueError, UnicodeError, TypeError):
            report["reason"] = "Container did not return one valid bounded result"
            report["stderr_tail"] = run.stderr[-300:]
            return report
        try:
            report["checks"]["original_fixture_unchanged"] = (
                fixture_hash(SOURCE) == baseline)
            report["checks"]["disposable_copy_restored"] = (
                fixture_hash(workspace) == baseline)
        except (ValueError, OSError):
            report["checks"]["original_fixture_unchanged"] = False
            report["checks"]["disposable_copy_restored"] = False
        if (not sentinel.is_file()
                or sentinel.read_text(encoding="utf-8") != "ONLY VISIBLE TO HOST"):
            report["reason"] = "Host-only sentinel was modified"
            return report
    if run.returncode != 0 or not checks_pass(report):
        report["reason"] = "At least one adversarial workspace guard failed"
        report["stderr_tail"] = run.stderr[-300:]
        return report
    report["status"] = "NEGATIVE_PRECHECK_PASS"
    return report


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--timeout", type=int, default=100)
    p.add_argument("--output", type=pathlib.Path,
                   default=ROOT / "artifacts/m0/workspace-negative.json")
    args = p.parse_args()
    if not 30 <= args.timeout <= 240:
        p.error("Timeout must be 30-240 seconds")
    result = probe(args.timeout)
    write_json(args.output, result)
    print(f'M0.10 DISPOSABLE SECURITY {result["status"]}')
    for name, good in sorted((result.get("checks") or {}).items()):
        print(f'  {name}: {"PASS" if good else "FAIL"}')
    if "container_exit_code" in result:
        print("Container exit code:", result["container_exit_code"])
    if "reason" in result:
        print("Reason:", result["reason"])
    if result.get("stderr_tail"):
        print("Container error (bounded):", result["stderr_tail"])
    print("Evidence:", args.output)
    print("No model, OpenCode, AI-generated command or benchmark was executed.")
    return 0 if result["status"] == "NEGATIVE_PRECHECK_PASS" else 2


if __name__ == "__main__":
    sys.exit(main())
