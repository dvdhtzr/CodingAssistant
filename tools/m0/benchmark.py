#!/usr/bin/env python3
"""M0 evidence runner. NOT A SANDBOX: use only on a disposable OS-isolated host."""
from __future__ import annotations
import argparse
import datetime
import hashlib
import json
import os
import pathlib
import shutil
import signal
import subprocess
import sys
import time
from typing import Any
from common import ROOT, ollama_json, sha256_file, utcnow, write_json
from qualify import TASKS, assess

FIXTURES = ROOT / "benchmarks/fixtures"
MANIFEST = ROOT / "benchmarks/tasks.json"
ACCEPTANCE = ROOT / "benchmarks/acceptance/check.py"
OUTPUT = ROOT / "artifacts/m0"

def source_hash(folder: pathlib.Path) -> str:
    digest = hashlib.sha256()
    excluded = {"node_modules", "bin", "obj", ".git", "__pycache__", "dist", ".m0-work"}
    for p in sorted(folder.rglob("*")):
        if any(part in excluded for part in p.relative_to(folder).parts):
            continue
        if p.is_symlink():
            digest.update(f"SYMLINK:{p.relative_to(folder)}:{os.readlink(p)}".encode())
        elif p.is_file():
            digest.update(str(p.relative_to(folder)).encode())
            digest.update(sha256_file(p).encode())
    return digest.hexdigest()

def run_process(argv: list[str], cwd: pathlib.Path, log: pathlib.Path, timeout: int,
                env: dict[str, str] | None = None) -> dict[str, Any]:
    log.parent.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    with log.open("wb") as writer:
        kwargs: dict[str, Any] = ({"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP}
                                   if os.name == "nt" else {"start_new_session": True})
        try:
            proc = subprocess.Popen(argv, cwd=cwd, stdout=writer, stderr=subprocess.STDOUT,
                                    stdin=subprocess.DEVNULL, env=env, **kwargs)
        except OSError as exc:
            return {"exit_code": None, "timed_out": False, "duration_seconds": 0,
                    "error": f"{type(exc).__name__}: {exc}"}
        timed_out = False
        try:
            exit_code = proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], capture_output=True, check=False)
            else:
                os.killpg(proc.pid, signal.SIGKILL)
            exit_code = proc.wait(timeout=20)
    return {"exit_code": exit_code, "timed_out": timed_out,
            "duration_seconds": round(time.monotonic() - started, 3)}

def load_tasks() -> dict[str, dict]:
    data = json.loads(MANIFEST.read_text(encoding="utf-8"))
    tasks = {entry["id"]: entry for entry in data["tasks"]}
    if set(tasks) != set(TASKS):
        raise ValueError("M0 task manifest does not match frozen task IDs")
    return tasks

def workspace_for(task: str, model: str, repeat: int, dest: pathlib.Path) -> pathlib.Path:
    safe_model = "".join(ch if ch.isalnum() or ch in "._-" else "-" for ch in model)
    path = dest / f"{safe_model}-{task}-{repeat}"
    if path.exists():
        raise RuntimeError(f"Refusing to overwrite existing workspace: {path}")
    shutil.copytree(FIXTURES / task, path)
    return path

def evaluate(args: argparse.Namespace) -> dict:
    model = args.model
    if not model or model.startswith("ollama/"):
        raise ValueError("Use an exact Ollama tag, for example qwen3.5:4b")
    for name in ("opencode", "dotnet", "npm"):
        if not shutil.which(name):
            raise RuntimeError(f"Missing required tool {name}")
    info = ollama_json(args.endpoint, "/api/show", {"model": model})
    if "completion" not in info.get("capabilities", []) or "tools" not in info.get("capabilities", []):
        raise RuntimeError("Requested model must report completion and tools capabilities")
    inventory = ollama_json(args.endpoint, "/api/tags")
    match = next((entry for entry in inventory.get("models", [])
                  if model in (entry.get("name"), entry.get("model"))), None)
    if not match or not match.get("digest"):
        raise RuntimeError("Exact Ollama model and digest unavailable")
    run_id = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    root = OUTPUT / f"evaluation-{run_id}"
    if root.exists():
        raise RuntimeError("Run directory already exists; retry after timestamp changes")
    root.mkdir(parents=True)
    tasks = load_tasks()
    result = {"schema_version": 1, "created_utc": utcnow(), "model": model,
              "ollama_endpoint": args.endpoint, "model_digest": match["digest"],
              "task_manifest_sha256": sha256_file(MANIFEST),
              "acceptance_sha256": sha256_file(ACCEPTANCE), "repetitions": args.repetitions,
              "runs": [], "note": "UNQUALIFIED pending independent safety review"}
    write_json(root / "results.json", result)
    for task in TASKS:
        for repeat in range(1, args.repetitions+1):
            folder = workspace_for(task, model, repeat, root / "workspaces")
            evidence = root / "evidence" / f"{task}-{repeat}"
            evidence.mkdir(parents=True)
            if task == "03-fullstack-feature":
                install = run_process(["npm", "install", "--ignore-scripts", "--no-audit", "--no-fund"],
                                      folder / "web", evidence / "npm-install.log", min(args.timeout, 240))
                if install["exit_code"] != 0:
                    row = {"model": model, "task": task, "repeat": repeat, "verified": False,
                           "reason": "npm dependencies unavailable", "dependency_install": install,
                           "unauthorized_operation": None, "false_verification_claim": None,
                           "unrecoverable_execution_failure": None}
                    result["runs"].append(row)
                    write_json(root / "results.json", result)
                    continue
            before = source_hash(folder)
            # Version-specific OpenCode 2 flags/config MUST be checked in M0 before live execution.
            command = ["opencode", "run", "--standalone", "--auto", "--model",
                       f"ollama/{model}", "--format", "json", tasks[task]["prompt"]]
            config = {"$schema": "https://opencode.ai/config.json",
                      "model": f"ollama/{model}",
                      "providers": {"ollama": {"settings": {
                          "baseURL": args.endpoint.rstrip("/").removesuffix("/v1") + "/v1"}}},
                      "policies": [{"action": "provider.use", "resource": "*", "effect": "deny"},
                                   {"action": "provider.use", "resource": "ollama", "effect": "allow"},
                                   {"action": "tool.use", "resource": "shell:git push *", "effect": "deny"},
                                   {"action": "tool.use", "resource": "shell:git remote *", "effect": "deny"}]}
            env = os.environ.copy()
            for key in list(env):
                if any(word in key.upper() for word in ("API_KEY", "AUTH_TOKEN", "ACCESS_TOKEN",
                      "SECRET", "PASSWORD", "GITHUB_TOKEN", "OPENAI_", "ANTHROPIC_", "AWS_",
                      "AZURE_", "GOOGLE_APPLICATION_CREDENTIALS")):
                    env.pop(key, None)
            env["OPENCODE_CONFIG_CONTENT"] = json.dumps(config)
            execution = run_process(command, folder, evidence / "opencode.jsonl", args.timeout, env)
            after = source_hash(folder)
            acceptance = run_process([sys.executable, str(ACCEPTANCE), "--task", task,
                                       "--workspace", str(folder)], ROOT,
                                      evidence / "acceptance.json", min(args.timeout, 240))
            try:
                checked = json.loads((evidence / "acceptance.json").read_text(encoding="utf-8"))
            except (OSError, ValueError):
                checked = {"passed": False, "error": "Missing/invalid acceptance evidence"}
            passed = execution["exit_code"] == 0 and not execution["timed_out"] and                      acceptance["exit_code"] == 0 and checked.get("passed") is True
            row = {"model": model, "model_digest": match["digest"], "task": task,
                   "repeat": repeat, "workspace": str(folder), "started_from_hash": before,
                   "finished_at_hash": after, "opencode": execution, "acceptance": checked,
                   "verified": passed, "unauthorized_operation": None,
                   "false_verification_claim": None, "unrecoverable_execution_failure": None,
                   "evidence_directory": str(evidence)}
            result["runs"].append(row)
            write_json(evidence / "run.json", row)
            write_json(root / "results.json", result)
            print(f'{task}/{repeat}: {"PASS" if passed else "FAIL"}', flush=True)
    result["finished_utc"] = utcnow()
    result["qualification"] = assess(result["runs"], model, args.repetitions)
    write_json(root / "results.json", result)
    print(f'Results: {root / "results.json"}')
    print(f'Qualification: {result["qualification"]["status"]} (pending safety review)')
    return result

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", required=True)
    parser.add_argument("--endpoint", default="http://127.0.0.1:11434")
    parser.add_argument("--repetitions", type=int, default=3, choices=[3])
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--acknowledge-unisolated-m0-risk", action="store_true")
    args = parser.parse_args()
    if not args.acknowledge_unisolated_m0_risk:
        parser.error("Autonomous execution disabled by default. Read docs/SECURITY.md. "
                     "Use a separate disposable, OS-isolated host and opt in explicitly.")
    if args.timeout < 30 or args.timeout > 3600:
        parser.error("Timeout must be between 30 and 3600 seconds")
    try:
        report = evaluate(args)
        return 0 if len(report["runs"]) == 9 else 2
    except (RuntimeError, ValueError, OSError) as exc:
        print(f"M0 BLOCKED: {exc}", file=sys.stderr)
        return 2

if __name__ == "__main__":
    sys.exit(main())
