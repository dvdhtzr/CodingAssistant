#!/usr/bin/env python3
"""M0.14 nine-run frozen fixture/isolation preflight, NO model or code execution.

Each of the approved three tasks is copied three times into a distinct, new
disposable workspace. A rootless, no-network Podman guest proves the intended
execution boundary and its child process; a host checks source hashes before
and after every run. This is a prerequisite, NOT the M0 benchmark or safety
audit. It never calls Ollama, launches OpenCode agents, or executes generated
code. The guest only checks versions and writes/removes a fixed marker.
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

from common import ROOT, sha256_file, utcnow, write_json
from controlled_repair_pilot import run_podman, exact_checks
from disposable_workspace_probe import command_for as frozen_guest_command
from qualify import TASKS
from runtime_image_probe import IMAGE
from sandbox_probe import image_status, inspect_podman

FIXTURES = ROOT / "benchmarks/fixtures"
MANIFEST = ROOT / "benchmarks/tasks.json"
CLIENT = pathlib.Path(__file__).with_name("qualification_fixture_client.mjs")
EXPECTED_FILES = {
    "01-api-feature": frozenset(("api/Api.csproj", "api/Program.cs")),
    "02-build-repair": frozenset(("api/Program.cs", "api/Repair.csproj")),
    "03-fullstack-feature": frozenset((
        "api/Api.csproj", "api/Program.cs", "web/package.json",
        "web/src/App.tsx", "web/tsconfig.json")),
}
REQUIRED = frozenset((
    "non_root", "no_new_privileges", "zero_capabilities",
    "root_read_only", "external_tcp_denied", "host_sentinel_hidden",
    "host_home_hidden", "no_git_control", "exact_frozen_files",
    "regular_unlinked_files", "bounded_fixture_bytes",
    "disposable_write_works", "disposable_write_cleaned",
    "child_non_root", "child_no_privileges", "child_no_new_privileges",
    "child_external_tcp_denied", "tools_present",
))


def sealed_snapshot(folder: pathlib.Path, task: str) -> str:
    """Exact approved file set, plain files only. Reject links and extra paths."""
    if (task not in EXPECTED_FILES or not folder.is_dir() or
            folder.is_symlink()):
        raise ValueError("Unknown task or linked source directory")
    files: set[str] = set()
    total = 0
    digest = hashlib.sha256()
    for item in sorted(folder.rglob("*")):
        relative = item.relative_to(folder)
        # Recheck parent entries, including linked directories and executables.
        if item.is_symlink():
            raise ValueError("Fixture contains a symbolic link")
        if item.is_dir():
            if item.stat().st_nlink < 1:
                raise ValueError("Invalid fixture directory")
            continue
        if not item.is_file() or item.stat().st_nlink != 1:
            raise ValueError("Fixture includes a nonregular/linked file")
        name = relative.as_posix()
        files.add(name)
        data = item.read_bytes()
        if not data or len(data) > 1024 * 1024:
            raise ValueError("Frozen file is empty or unbounded")
        total += len(data)
        if total > 4 * 1024 * 1024:
            raise ValueError("Frozen fixture too large")
        digest.update(name.encode("utf-8") + b"\0" + data + b"\0")
    if files != EXPECTED_FILES[task]:
        raise ValueError("Frozen fixture file set changed")
    return digest.hexdigest()


def check_manifest() -> str:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    entries = manifest.get("tasks")
    if (type(entries) is not list or len(entries) != 3 or
            [entry.get("id") for entry in entries] != list(TASKS) or
            any(type(entry.get("prompt")) is not str or not entry["prompt"]
                for entry in entries)):
        raise ValueError("Original frozen three-task manifest changed")
    if set(EXPECTED_FILES) != set(TASKS):
        raise ValueError("Unknown qualification task")
    return sha256_file(MANIFEST)


def guest_command(image_id: str, uid: int, gid: int,
                  workspace: pathlib.Path, sentinel: pathlib.Path,
                  task: str) -> list[str]:
    if task not in EXPECTED_FILES:
        raise ValueError("Invalid qualification task")
    cmd = frozen_guest_command(image_id, uid, gid, workspace, sentinel)
    cmd[cmd.index("--eval") + 1] = CLIENT.read_text(encoding="utf-8")
    cmd[cmd.index("--memory=512m")] = "--memory=2g"
    cmd[cmd.index("--pids-limit=64")] = "--pids-limit=96"
    cmd[cmd.index("--tmpfs=/tmp:rw,nosuid,nodev,size=64m,notmpcopyup")] = (
        "--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup")
    cmd.extend([task])  # Node's `--` sentinel arg already precedes this.
    # Guest CLI consumes: model-free --eval <script> -- <sentinel> <task>.
    # Insert task first so process.argv.slice(1) == [task, sentinel].
    cmd[-2], cmd[-1] = cmd[-1], cmd[-2]
    return cmd


def evaluate(timeout: int, output: pathlib.Path) -> dict[str, Any]:
    report: dict[str, Any] = {
        "schema_version": 1, "stage": "M0.14-fixture-isolation",
        "status": "BLOCKED", "recorded_at_utc": utcnow(),
        "purpose": "3 tasks x 3 frozen isolated fixture copies, no model or builds",
        "runs": [], "model_used": False, "generated_code_executed": False,
        "qualification": "NOT_RUN",
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        report["reason"] = "Requires Linux non-root rootless Podman"
        return report
    try:
        report["manifest_sha256"] = check_manifest()
        baselines = {task: sealed_snapshot(FIXTURES / task, task)
                     for task in TASKS}
        report["fixture_sha256"] = baselines
    except (OSError, ValueError, TypeError, KeyError) as exc:
        report["reason"] = f"Frozen task inputs invalid: {type(exc).__name__}"
        return report
    if inspect_podman(20).get("rootless") is not True:
        report["reason"] = "Rootless Podman unavailable"
        return report
    image = image_status(IMAGE, 20)
    if not image.get("present") or not image.get("image_id"):
        report["reason"] = "Previously verified immutable offline toolchain absent"
        return report
    report["image_id"] = image["image_id"]
    for task in TASKS:
        for repeat in range(1, 4):
            row: dict[str, Any] = {
                "task": task, "repeat": repeat, "status": "BLOCKED",
                "started_from_hash": baselines[task],
            }
            report["runs"].append(row)
            with tempfile.TemporaryDirectory(prefix="ca-m014-matrix-") as temp:
                root = pathlib.Path(temp)
                workspace = root / "workspace"
                sentinel = root / "host-only"
                sentinel.write_text("not mounted", encoding="utf-8")
                shutil.copytree(FIXTURES / task, workspace, symlinks=False)
                work = root / "container-evidence"
                work.mkdir(mode=0o700)
                try:
                    if sealed_snapshot(workspace, task) != baselines[task]:
                        raise ValueError("Disposable source changed on copy")
                    cmd = guest_command(image["image_id"], os.getuid(), os.getgid(),
                                        workspace, sentinel, task)
                    result = run_podman(cmd, work, timeout)
                    observed = json.loads(result.stdout.strip())
                    row["container_exit_code"] = result.returncode
                    row["checks"] = observed.get("checks")
                    row["finished_at_hash"] = sealed_snapshot(workspace, task)
                    row["original_still_sealed"] = (
                        sealed_snapshot(FIXTURES / task, task) == baselines[task])
                    row["host_sentinel_untouched"] = (
                        sentinel.read_text(encoding="utf-8") == "not mounted")
                    if (observed.get("task") != task or result.returncode != 0 or
                            not exact_checks(observed, REQUIRED) or
                            row["finished_at_hash"] != baselines[task] or
                            not row["original_still_sealed"] or
                            not row["host_sentinel_untouched"]):
                        raise ValueError("Guest isolation or fixture integrity failure")
                    row["status"] = "PASS"
                except (ValueError, OSError, TypeError, KeyError,
                        subprocess.TimeoutExpired) as exc:
                    row["reason"] = type(exc).__name__
                    write_json(output, report)
                    return report
            write_json(output, report)
            print(f'M0.14 {task}/{repeat}: isolated fixture PASS', flush=True)
    report["status"] = "ISOLATION_MATRIX_PASS"
    report["completed_utc"] = utcnow()
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--timeout-per-run", type=int, default=90)
    parser.add_argument("--output", type=pathlib.Path, default=(
        ROOT / "artifacts/m0/qualification-isolation.json"))
    args = parser.parse_args()
    if not 40 <= args.timeout_per_run <= 180:
        parser.error("Isolation time budget must be 40..180 seconds per run")
    report = evaluate(args.timeout_per_run, args.output)
    write_json(args.output, report)
    print(f'M0.14 FROZEN QUALIFICATION ISOLATION {report["status"]}')
    print("Completed:", sum(x["status"] == "PASS" for x in report["runs"]), "/ 9")
    if report.get("reason"):
        print("Reason:", report["reason"])
    elif report["status"] != "ISOLATION_MATRIX_PASS":
        for x in report["runs"]:
            if x["status"] != "PASS":
                print(f'Blocked: {x["task"]}/{x["repeat"]}: {x.get("reason")}')
                break
    print("Evidence:", args.output)
    print("No Qwen, autonomous OpenCode agent, generated code, tests or builds ran.")
    print("Full M0 3x3 model qualification and independent safety audit still pending.")
    return 0 if report["status"] == "ISOLATION_MATRIX_PASS" else 2


if __name__ == "__main__":
    sys.exit(main())
