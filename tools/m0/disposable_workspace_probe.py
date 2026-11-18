#!/usr/bin/env python3
"""M0.9 non-agent disposable fixture workspace and child-process preflight.

Uses only an existing pinned local image, one NEW temp fixture copy, rootless
Podman, and a deterministic Node script. Never executes model-generated code,
OpenCode, npm, dotnet, or a benchmark. M0/M1 remain unqualified.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
from typing import Any

from common import ROOT, utcnow, write_json
from runtime_image_probe import IMAGE
from sandbox_probe import inspect_podman, image_status

SOURCE = ROOT / "benchmarks/fixtures/02-build-repair/api"
CLIENT = pathlib.Path(__file__).with_name("disposable_workspace_client.mjs")
EXPECTED = frozenset(("Program.cs", "Repair.csproj"))
REQUIRED = frozenset((
    "non_root", "root_read_only", "no_effective_capabilities",
    "no_new_privileges", "fixture_readable", "temporary_workspace_write",
    "host_sentinel_hidden", "no_external_tcp", "child_non_root",
    "child_root_read_only", "child_no_effective_capabilities",
    "child_no_new_privileges", "child_host_sentinel_hidden",
    "child_no_external_tcp", "fixture_original_unchanged",
    "workspace_restored_to_initial_hash",
))


def fixture_hash(folder: pathlib.Path) -> str:
    """Refuse symlinks, hard-linked files, unexpected paths and large inputs."""
    if folder.is_symlink() or not folder.is_dir():
        raise ValueError("Fixture directory missing or linked")
    names = {p.name for p in folder.iterdir()}
    if names != EXPECTED:
        raise ValueError("Frozen fixture contents differ from approved baseline")
    h = hashlib.sha256()
    total = 0
    for name in sorted(names):
        p = folder / name
        if p.is_symlink() or not p.is_file() or p.stat().st_nlink != 1:
            raise ValueError("Fixture must contain plain, non-linked files")
        data = p.read_bytes()
        total += len(data)
        if not data or total > 1024 * 1024:
            raise ValueError("Fixture bytes outside preflight bounds")
        h.update(name.encode("utf-8") + b"\0" + data + b"\0")
    return h.hexdigest()


def command_for(image_id: str, uid: int, gid: int, workspace: pathlib.Path,
                host_sentinel: pathlib.Path) -> list[str]:
    if not re.fullmatch(r"(?:sha256:)?[a-fA-F0-9]{64}", image_id):
        raise ValueError("Immutable local image ID required")
    if not workspace.is_dir() or not host_sentinel.is_file():
        raise ValueError("Disposable fixture and host sentinel required")
    # No source repository, Git remote, socket or host home is mounted.
    return [
        "podman", "run", "--rm", "--pull=never", "--network=none",
        "--read-only", "--cap-drop=all", "--security-opt=no-new-privileges",
        "--pids-limit=64", "--memory=512m", "--userns=keep-id",
        "--user", f"{uid}:{gid}",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=64m,notmpcopyup",
        "--volume", f"{workspace.resolve()}:/workspace:rw",
        "--env", "HOME=/tmp", "--env", "XDG_CONFIG_HOME=/tmp/.config",
        "--workdir=/workspace", "--entrypoint=node", image_id,
        "--input-type=module", "--eval", CLIENT.read_text(encoding="utf-8"),
        "--", str(host_sentinel.resolve()),
    ]


def checks_pass(report: dict[str, Any]) -> bool:
    checks = report.get("checks")
    return (type(checks) is dict and set(checks) == REQUIRED
            and all(checks[name] is True for name in REQUIRED))


def probe(timeout: int) -> dict[str, Any]:
    report: dict[str, Any] = {
        "schema_version": 1, "stage": "M0.9",
        "recorded_at_utc": utcnow(), "status": "BLOCKED",
        "purpose": "fixture workspace isolation only; no model or agent execution",
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        report["reason"] = "Requires non-root Linux and rootless Podman"
        return report
    if not inspect_podman(20).get("rootless"):
        report["reason"] = "Podman does not report rootless operation"
        return report
    image = image_status(IMAGE, 20)
    if not image.get("present") or not image.get("image_id"):
        report["reason"] = "Previously verified local offline image is absent"
        return report
    report["image_id"] = image["image_id"]
    try:
        original_hash = fixture_hash(SOURCE)
    except (ValueError, OSError) as exc:
        report["reason"] = f"Input fixture rejected: {type(exc).__name__}"
        return report
    report["fixture_sha256"] = original_hash
    with tempfile.TemporaryDirectory(prefix="ca-m09-workspace-") as temporary:
        parent = pathlib.Path(temporary)
        workspace = parent / "workspace"
        sentinel = parent / "host-sentinel-not-mounted"
        sentinel.write_text("unmounted host file", encoding="utf-8")
        shutil.copytree(SOURCE, workspace, symlinks=False)
        if fixture_hash(workspace) != original_hash:
            report["reason"] = "Disposable fixture differs from original"
            return report
        try:
            cmd = command_for(image["image_id"], os.getuid(), os.getgid(),
                              workspace, sentinel)
            result = subprocess.run(cmd, stdin=subprocess.DEVNULL, check=False,
                                    capture_output=True, text=True, encoding="utf-8",
                                    errors="replace", timeout=timeout)
        except (OSError, ValueError, subprocess.TimeoutExpired) as exc:
            report["reason"] = f"Podman preflight failed: {type(exc).__name__}"
            return report
        report["container_exit_code"] = result.returncode
        try:
            guest = json.loads(result.stdout.strip())
            checks = guest.get("checks")
            if type(checks) is not dict:
                raise ValueError("Missing guest checks")
            report["checks"] = checks
        except (ValueError, TypeError, AttributeError):
            report["reason"] = "No valid guest preflight status"
            report["stderr_tail"] = result.stderr[-250:]
            return report
        report["checks"]["fixture_original_unchanged"] = fixture_hash(SOURCE) == original_hash
        report["checks"]["workspace_restored_to_initial_hash"] = (
            fixture_hash(workspace) == original_hash
        )
        if not sentinel.exists() or sentinel.read_text(encoding="utf-8") != "unmounted host file":
            report["reason"] = "Host sentinel was unexpectedly modified"
            return report
    if result.returncode != 0 or not checks_pass(report):
        report["reason"] = "One or more workspace isolation guards failed"
        report["stderr_tail"] = result.stderr[-250:]
        return report
    report["status"] = "WORKSPACE_PRECHECK_PASS"
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--timeout", type=int, default=90)
    parser.add_argument("--output", type=pathlib.Path,
                        default=ROOT / "artifacts/m0/disposable-workspace.json")
    args = parser.parse_args()
    if not 20 <= args.timeout <= 300:
        parser.error("Timeout must be 20–300 seconds")
    result = probe(args.timeout)
    write_json(args.output, result)
    print(f'M0.9 DISPOSABLE WORKSPACE {result["status"]}')
    for name, okay in sorted((result.get("checks") or {}).items()):
        print(f'  {name}: {"PASS" if okay else "FAIL"}')
    if "container_exit_code" in result:
        print("Container exit code:", result["container_exit_code"])
    if result.get("reason"):
        print("Reason:", result["reason"])
    if result.get("stderr_tail"):
        print("Container error (bounded):", result["stderr_tail"])
    print("Evidence:", args.output)
    print("No OpenCode, model inference, generated code or benchmark was executed.")
    return 0 if result["status"] == "WORKSPACE_PRECHECK_PASS" else 2


if __name__ == "__main__":
    sys.exit(main())
