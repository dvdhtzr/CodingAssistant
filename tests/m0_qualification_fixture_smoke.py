#!/usr/bin/env python3
"""M0.14 CI: three real, no-model OS-isolated fixture copies (one per task).

Docker here verifies the portable containment contract in GitHub CI. Dell
must separately run the full nine-run **rootless Podman** preflight. No
model, network, generated code, build, or acceptance is performed here.
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

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))

from qualification_isolation_probe import (
    CLIENT, EXPECTED_FILES, REQUIRED, FIXTURES, sealed_snapshot, exact_checks,
)
from qualify import TASKS


def smoke(image: str) -> None:
    for task in TASKS:
        original = sealed_snapshot(FIXTURES / task, task)
        with tempfile.TemporaryDirectory(prefix="ca-m014-ci-") as temp:
            root = pathlib.Path(temp)
            workspace = root / "workspace"
            sentinel = root / "not-mounted"
            sentinel.write_text("hidden", encoding="utf-8")
            shutil.copytree(FIXTURES / task, workspace)
            cmd = [
                "docker", "run", "--rm", "--pull", "never",
                "--network", "none", "--read-only", "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges",
                "--pids-limit", "96", "--memory", "2g",
                "--tmpfs", "/tmp:rw,nosuid,nodev,size=512m",
                "--user", f"{os.getuid()}:{os.getgid()}",
                "--volume", f"{workspace.resolve()}:/workspace:rw",
                "--env", "HOME=/tmp", "--env", "DOTNET_CLI_HOME=/tmp",
                "--env", "XDG_CONFIG_HOME=/tmp/.config",
                "--env", "OPENCODE_DISABLE_AUTOUPDATE=1",
                "--workdir", "/workspace", "--entrypoint", "node",
                image, "--input-type=module", "--eval",
                CLIENT.read_text(encoding="utf-8"), "--", task,
                str(sentinel.resolve()),
            ]
            result = subprocess.run(
                cmd, stdin=subprocess.DEVNULL, capture_output=True,
                text=True, encoding="utf-8", errors="replace",
                timeout=90, check=False,
            )
            try:
                evidence = json.loads(result.stdout.strip())
            except ValueError:
                evidence = {}
            if (result.returncode != 0 or evidence.get("task") != task or
                    not exact_checks(evidence, REQUIRED) or
                    sealed_snapshot(workspace, task) != original or
                    sealed_snapshot(FIXTURES / task, task) != original or
                    sentinel.read_text(encoding="utf-8") != "hidden"):
                raise RuntimeError(
                    "Fixture isolation failed for " + task + "\n" +
                    result.stderr[-300:] + "\n" + result.stdout[-1200:])
            print("M0.14 " + task + ": isolated fixture CI PASS", flush=True)
    print("M0.14 THREE_TASK_ISOLATION_CI_PASS (NO MODEL; 3x3 DELL PENDING)")


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--image", default="codingassistant-m0-runtime:ci")
    args = p.parse_args()
    try:
        smoke(args.image)
    except (RuntimeError, OSError, ValueError, subprocess.TimeoutExpired) as exc:
        print("M0.14 CI BLOCKED:", str(exc)[:2000], file=sys.stderr)
        raise SystemExit(2) from None
