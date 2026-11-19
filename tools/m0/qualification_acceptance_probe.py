#!/usr/bin/env python3
"""M0 qualification: fixed OS-contained independent behavior evaluator adapter.

This module exposes a no-network, non-root Podman evaluator for model-generated
disposable workspaces. The evaluator has no Ollama socket or tools, and never
operates on frozen source paths. The model benchmark runner will call this
adapter after each real OpenCode run. This module is NOT the benchmark runner.
"""
from __future__ import annotations

import json
import os
import pathlib
import re
import subprocess
from typing import Any

from common import ROOT
from controlled_repair_pilot import run_podman
from disposable_workspace_probe import command_for as sandbox_command
from qualify import TASKS

CLIENT = pathlib.Path(__file__).with_name("qualification_acceptance_client.mjs")
IMAGE = "localhost/codingassistant-m0-qualification:1"
GUARDS = frozenset((
    "non_root", "no_new_privileges", "zero_capabilities", "root_read_only",
    "external_tcp_denied", "host_sentinel_hidden", "host_git_hidden",
    "inference_socket_hidden", "original_sources_regular",
    "trusted_tests_available", "network_not_required",
))
CHECKS = frozenset((
    "offline_restore", "offline_build", "health", "feature_behavior",
    "frontend_typecheck", "frontend_behavior", "injected_test_cleaned",
    "offline_node_dependencies",
))


def evaluator_command(image_id: str, uid: int, gid: int,
                      workspace: pathlib.Path, sentinel: pathlib.Path,
                      task: str) -> list[str]:
    if task not in TASKS:
        raise ValueError("Unknown frozen qualification task")
    if (workspace.is_symlink() or not workspace.is_dir() or
            not sentinel.is_file() or sentinel.is_symlink()):
        raise ValueError("Evaluator requires a regular disposable workspace")
    # No whole host repo, host home, model socket, or external network.
    args = sandbox_command(image_id, uid, gid, workspace, sentinel)
    args[args.index("--eval") + 1] = CLIENT.read_text(encoding="utf-8")
    args[args.index("--memory=512m")] = "--memory=3g"
    args[args.index("--pids-limit=64")] = "--pids-limit=96"
    args[args.index("--tmpfs=/tmp:rw,nosuid,nodev,size=64m,notmpcopyup")] = (
        "--tmpfs=/tmp:rw,nosuid,nodev,size=768m,notmpcopyup")
    args[-1:] = ["--", task, str(sentinel.resolve())] if args[-2] != "--" else [
        task, str(sentinel.resolve())]
    # sandbox_command already provides '--' and one sentinel; replace just
    # that sentinel with the two fixed evaluator arguments.
    if args.count("--") != 1:
        raise ValueError("Guest arguments malformed")
    return args


def validate_result(report: object, task: str) -> bool:
    if type(report) is not dict or report.get("task") != task:
        return False
    guards = report.get("guards")
    checks = report.get("checks")
    if (type(guards) is not dict or set(guards) != GUARDS or
            type(checks) is not dict or set(checks) != CHECKS or
            type(report.get("verified")) is not bool):
        return False
    if any(type(guards[key]) is not bool for key in GUARDS):
        return False
    if any(type(checks[key]) is not bool for key in CHECKS):
        return False
    expected = all(guards.values()) and all(checks.values())
    return report["verified"] is expected


def run_evaluator(image_id: str, task: str, workspace: pathlib.Path,
                  sentinel: pathlib.Path, evidence: pathlib.Path,
                  timeout: int = 300) -> dict[str, Any]:
    if not 120 <= timeout <= 600:
        raise ValueError("Evaluator timeout must be 120..600 seconds")
    command = evaluator_command(image_id, os.getuid(), os.getgid(),
                                workspace, sentinel, task)
    evidence.mkdir(mode=0o700, parents=True, exist_ok=False)
    result = run_podman(command, evidence, timeout)
    try:
        output = json.loads(result.stdout.strip())
        if not validate_result(output, task):
            raise ValueError("Invalid evaluator JSON contract")
    except (ValueError, TypeError, AttributeError):
        return {"status": "INVALID_EVIDENCE", "verified": False,
                "exit_code": result.returncode,
                "stderr_tail": result.stderr[-240:]}
    if result.returncode not in (0, 2):
        return {"status": "ENVIRONMENT_BLOCKED", "verified": False,
                "exit_code": result.returncode}
    return {"status": "VERIFIED" if output["verified"] and result.returncode == 0
            else "ACCEPTANCE_FAILED", "verified": output["verified"]
            and result.returncode == 0, "exit_code": result.returncode,
            "guards": output["guards"], "checks": output["checks"],
            "notes": output.get("notes", [])}
