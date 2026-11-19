#!/usr/bin/env python3
"""CI-only fixed known-good baseline tests for the M0 independent evaluator.

No model is used. All *trusted*, deterministic source substitutions are made
in disposable copies only. All builds and generated programs execute in an
offline non-root container with no credentials and no inference socket.
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
sys.path.insert(0, str(ROOT / "tools/m0"))
from qualification_acceptance_probe import CLIENT, GUARDS, CHECKS, validate_result
from qualification_isolation_probe import FIXTURES, sealed_snapshot
from qualify import TASKS

GOOD_API = """var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/math/add", (string? a, string? b) =>
    int.TryParse(a, out var x) && int.TryParse(b, out var y)
        ? Results.Ok(new { sum = x + y }) : Results.BadRequest());
app.Run();
"""
GOOD_SUMMARY = """var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/tasks/summary", () => Results.Ok(new { total = 3, completed = 2 }));
app.Run();
"""
GOOD_REACT = """import {useEffect, useState} from 'react';
export function TaskSummary() {
  const [state, setState] = useState<{total: number; completed: number}|null>(null);
  const [error, setError] = useState<string|null>(null);
  useEffect(() => {
    fetch('/api/tasks/summary')
      .then(response => { if (!response.ok) throw new Error('bad status'); return response.json(); })
      .then(setState)
      .catch(() => setError('Unable to load tasks'));
  }, []);
  if (error) return <p role="alert">{error}</p>;
  if (!state) return <p>Loading tasks</p>;
  return <p>{state.completed} of {state.total} tasks complete</p>;
}
"""


def docker_check(image: str, task: str, workspace: pathlib.Path,
                 sentinel: pathlib.Path) -> dict:
    cmd = [
        "docker", "run", "--rm", "--pull", "never",
        "--network", "none", "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges", "--pids-limit", "96",
        "--memory", "3g", "--tmpfs", "/tmp:rw,nosuid,nodev,size=768m",
        "--user", f"{os.getuid()}:{os.getgid()}",
        "--volume", f"{workspace.resolve()}:/workspace:rw",
        "--env", "HOME=/tmp", "--env", "DOTNET_CLI_HOME=/tmp",
        "--workdir", "/workspace", "--entrypoint", "node",
        image, "--input-type=module", "--eval",
        CLIENT.read_text(encoding="utf-8"), "--", task, str(sentinel.resolve()),
    ]
    result = subprocess.run(cmd, stdin=subprocess.DEVNULL, capture_output=True,
                            text=True, encoding="utf-8", errors="replace",
                            timeout=330, check=False)
    try:
        observed = json.loads(result.stdout.strip())
    except ValueError:
        raise RuntimeError("No valid evaluator JSON: " +
                           result.stderr[-300:] + " " + result.stdout[-300:])
    if not validate_result(observed, task) or result.returncode not in (0, 2):
        raise RuntimeError("Invalid independent evaluation contract: " +
                           json.dumps(observed)[:1600] + result.stderr[-250:])
    return observed


def smoke(image: str) -> None:
    for task in TASKS:
        original_hash = sealed_snapshot(FIXTURES / task, task)
        with tempfile.TemporaryDirectory(prefix="ca-m015-acceptance-") as temp:
            base = pathlib.Path(temp)
            workspace = base / "workspace"
            shutil.copytree(FIXTURES / task, workspace)
            sentinel = base / "host-only"
            sentinel.write_text("hidden", encoding="utf-8")
            if task == "01-api-feature":
                (workspace / "api" / "Program.cs").write_text(GOOD_API, encoding="utf-8")
            elif task == "02-build-repair":
                source = workspace / "api" / "Program.cs"
                code = source.read_text(encoding="utf-8")
                assert code.count("values.Summ()") == 1
                source.write_text(code.replace("values.Summ()", "values.Sum()"),
                                  encoding="utf-8")
            else:
                (workspace / "api" / "Program.cs").write_text(GOOD_SUMMARY, encoding="utf-8")
                (workspace / "web/src/TaskSummary.tsx").write_text(GOOD_REACT,
                                                                     encoding="utf-8")
            observed = docker_check(image, task, workspace, sentinel)
            if not observed["verified"]:
                raise RuntimeError("Known-good baseline failed: " + task + ": " +
                                   json.dumps(observed)[:1800])
            if (sealed_snapshot(FIXTURES / task, task) != original_hash or
                    sentinel.read_text(encoding="utf-8") != "hidden" or
                    (workspace / "web/node_modules").exists() or
                    (workspace / "web/src/TaskSummary.acceptance.test.tsx").exists()):
                raise RuntimeError("Source/sentinel integrity changed: " + task)
            print("M0.15 " + task + ": offline acceptance PASS", flush=True)
    # A deliberately broken model output must be scored FAIL, not PASS.
    with tempfile.TemporaryDirectory(prefix="ca-m015-negative-") as temp:
        base = pathlib.Path(temp)
        workspace = base / "workspace"
        shutil.copytree(FIXTURES / "02-build-repair", workspace)
        sentinel = base / "host-only"
        sentinel.write_text("hidden", encoding="utf-8")
        observed = docker_check(image, "02-build-repair", workspace, sentinel)
        if observed["verified"] or observed["checks"]["offline_build"]:
            raise RuntimeError("Known broken C# repair incorrectly passed")
    print("M0.15 KNOWN_BAD_BUILD_DENIED")
    print("M0.15 INDEPENDENT_OFFLINE_ACCEPTANCE_CI_PASS (NO MODEL)")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--image", default="codingassistant-m0-qualification:ci")
    args = ap.parse_args()
    try:
        smoke(args.image)
    except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as exc:
        print("M0.15 BLOCKED:", str(exc)[:2000], file=sys.stderr)
        raise SystemExit(2) from None
