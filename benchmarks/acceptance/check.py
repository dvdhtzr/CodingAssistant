#!/usr/bin/env python3
"""Independent M0 acceptance checks: keep OUTSIDE the agent's writable workspace."""
from __future__ import annotations
import argparse
import json
import os
import pathlib
import re
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from typing import Any

def execute(argv: list[str], cwd: pathlib.Path, timeout: int = 150) -> dict[str, Any]:
    try:
        p = subprocess.run(argv, cwd=cwd, timeout=timeout, text=True, encoding="utf-8",
                           errors="replace", stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT, check=False)
        return {"passed": p.returncode == 0, "exit_code": p.returncode, "output_tail": p.stdout[-12000:]}
    except (OSError, subprocess.TimeoutExpired) as exc:
        return {"passed": False, "error": f"{type(exc).__name__}: {exc}"}

def available_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]

def get_json(url: str) -> tuple[int, Any]:
    try:
        with urllib.request.urlopen(url, timeout=2) as r:
            return r.status, json.loads(r.read(100000).decode("utf-8"))
    except urllib.error.HTTPError as exc:
        return exc.code, None

def check_api(workspace: pathlib.Path, task: str) -> dict:
    build = execute(["dotnet", "build", "--nologo", "-v:q"], workspace / "api")
    if not build["passed"]:
        return {"passed": False, "build": build, "checks": []}
    port = available_port()
    env = os.environ.copy()
    env["ASPNETCORE_URLS"] = f"http://127.0.0.1:{port}"
    proc = subprocess.Popen(["dotnet", str(workspace / "api/bin/Debug/net10.0/Api.dll")],
                            cwd=workspace / "api", env=env, stdout=subprocess.DEVNULL,
                            stderr=subprocess.DEVNULL)
    checks: list[dict] = []
    try:
        for _ in range(70):
            if proc.poll() is not None:
                break
            try:
                get_json(f"http://127.0.0.1:{port}/health")
                break
            except (OSError, ValueError):
                time.sleep(0.1)
        def check(name: str, url: str, status: int, expected: Any = None) -> None:
            try:
                actual, body = get_json(f"http://127.0.0.1:{port}{url}")
                checks.append({"name": name, "passed": actual == status and
                              (expected is None or body == expected), "status": actual, "body": body})
            except (OSError, ValueError) as exc:
                checks.append({"name": name, "passed": False, "error": str(exc)})
        check("health", "/health", 200, {"status": "ok"})
        if task == "01-api-feature":
            check("addition", "/api/math/add?a=2&b=3", 200, {"sum": 5})
            check("negative", "/api/math/add?a=-4&b=3", 200, {"sum": -1})
            check("missing", "/api/math/add?a=1", 400)
            check("invalid", "/api/math/add?a=xyz&b=3", 400)
        else:
            check("summary", "/api/tasks/summary", 200, {"total": 3, "completed": 2})
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=4)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=4)
    return {"passed": bool(checks) and all(c["passed"] for c in checks),
            "build": build, "checks": checks}

def check_repair(workspace: pathlib.Path) -> dict:
    out = execute(["dotnet", "run", "--project", str(workspace / "api/Repair.csproj")], workspace)
    return {"passed": out["passed"] and out.get("output_tail", "").strip() == "Total: 15", "run": out}

def check_react(workspace: pathlib.Path) -> dict:
    web = workspace / "web"
    component = web / "src/TaskSummary.tsx"
    if not component.is_file():
        return {"passed": False, "reason": "Missing TaskSummary.tsx"}
    content = component.read_text(encoding="utf-8")
    static = {
        "exported_component": bool(re.search(
            r"export\s+(default\s+)?(function|const|class)\s+TaskSummary\b|export\s*\{[^}]*\bTaskSummary\b", content)),
        "fetches_summary": "/api/tasks/summary" in content}
    if not all(static.values()):
        return {"passed": False, "static_checks": static}
    suffix = ".cmd" if os.name == "nt" else ""
    tsc = web / "node_modules/.bin" / ("tsc" + suffix)
    vitest = web / "node_modules/.bin" / ("vitest" + suffix)
    if not (tsc.exists() and vitest.exists()):
        return {"passed": False, "reason": "Missing web dependencies", "static_checks": static}
    compile_result = execute([str(tsc), "--noEmit"], web)
    if not compile_result["passed"]:
        return {"passed": False, "typecheck": compile_result, "static_checks": static}
    acceptance = web / "src/TaskSummary.acceptance.test.tsx"
    if acceptance.exists():
        return {"passed": False, "reason": "Acceptance test unexpectedly in workspace"}
    try:
        shutil.copyfile(pathlib.Path(__file__).with_name("TaskSummary.acceptance.test.tsx"), acceptance)
        behavior = execute([str(vitest), "run", "--environment", "jsdom",
                            "src/TaskSummary.acceptance.test.tsx"], web)
    finally:
        acceptance.unlink(missing_ok=True)
    return {"passed": behavior["passed"], "typecheck": compile_result,
            "behavior": behavior, "static_checks": static}

def check(task: str, workspace: pathlib.Path) -> dict:
    if not workspace.exists():
        return {"passed": False, "reason": "Missing workspace"}
    if task == "02-build-repair":
        return check_repair(workspace)
    api = check_api(workspace, task)
    if task == "01-api-feature":
        return {"passed": api["passed"], "api": api}
    if task == "03-fullstack-feature":
        web = check_react(workspace)
        return {"passed": api["passed"] and web["passed"], "api": api, "web": web}
    return {"passed": False, "reason": "Unknown task"}

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--task", required=True,
                        choices=["01-api-feature", "02-build-repair", "03-fullstack-feature"])
    parser.add_argument("--workspace", required=True, type=pathlib.Path)
    args = parser.parse_args()
    report = check(args.task, args.workspace.resolve())
    print(json.dumps(report, indent=2))
    return 0 if report["passed"] else 1

if __name__ == "__main__":
    sys.exit(main())
