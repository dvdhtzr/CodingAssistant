#!/usr/bin/env python3
"""M0.16 actual native OpenCode writes in Docker with MOCK tool decisions.

One independent disposable workspace per frozen task, real OpenCode CLI, real
permission gates and real offline C# / React acceptance. ONLY the host Ollama
model answer and model metadata are deterministic mocks. This is NOT an
M0 real-model qualification pass.
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
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))

from ollama_bridge import serve_bridge
from qualification_isolation_probe import FIXTURES, sealed_snapshot
from qualify import TASKS
from pinned_qualification import FILES, pinned_qualification_handler, task_prompt
from real_qualification import CLIENT, CHECKS, scan_workspace
from qualification_acceptance_probe import validate_result
from m0_qualification_acceptance_smoke import GOOD_API, GOOD_SUMMARY, GOOD_REACT

MODEL = "qwen3.5:4b"
DIGEST = "d" * 64
DETAILS = {"format": "gguf", "family": "qwen3.5",
           "families": ["qwen3.5"], "parameter_size": "4B",
           "quantization_level": "Q4_K_M"}


def docker_write(image: str, task: str, workspace: pathlib.Path,
                 private: pathlib.Path, sentinel: pathlib.Path,
                 compile_feedback: tuple[str, ...] = ()) -> subprocess.CompletedProcess:
    prompt = task_prompt(task)
    if compile_feedback:
        prompt += ("\nA separate, offline .NET build failed with: " +
                   json.dumps(compile_feedback) +
                   "\nRepair the original task in the same disposable source file.")
    cmd = ["docker", "run", "--rm", "--pull", "never",
           "--network", "none", "--read-only", "--cap-drop", "ALL",
           "--security-opt", "no-new-privileges", "--pids-limit", "96",
           "--memory", "3g", "--tmpfs", "/tmp:rw,nosuid,nodev,size=768m",
           "--user", f"{os.getuid()}:{os.getgid()}",
           "--volume", f"{workspace.resolve()}:/workspace:rw",
           "--volume", f"{private.resolve()}:/run/codingassistant:ro",
           "--env", "HOME=/tmp", "--env", "XDG_CONFIG_HOME=/tmp/.config",
           "--env", "DOTNET_CLI_HOME=/tmp",
           "--workdir", "/workspace", "--entrypoint", "node",
           image, "--input-type=module", "--eval",
           CLIENT.read_text(encoding="utf-8"), "--", MODEL, task,
           prompt, str(sentinel.resolve())]
    return subprocess.run(cmd, stdin=subprocess.DEVNULL,
                          capture_output=True, encoding="utf-8", text=True,
                          errors="replace", timeout=360, check=False)


def docker_accept(image: str, task: str, workspace: pathlib.Path,
                  sentinel: pathlib.Path) -> dict:
    from m0_qualification_acceptance_smoke import docker_check
    return docker_check(image, task, workspace, sentinel)


def smoke(runtime: str, evaluator: str) -> None:
    details = {"capabilities": ["completion", "tools"],
               "details": DETAILS,
               "model_info": {"qwen3.5.context_length": 32768}}
    tags = {"models": [{"name": MODEL, "model": MODEL, "digest": DIGEST,
            "modified_at": "2026-11-19T00:00:00Z", "size": 2000000000,
            "details": DETAILS}]}
    for task in TASKS:
        original_hash = sealed_snapshot(FIXTURES / task, task)
        with tempfile.TemporaryDirectory(prefix="ca-m016-ci-") as temp:
            parent = pathlib.Path(temp)
            workspace = parent / "workspace"
            shutil.copytree(FIXTURES / task, workspace)
            private = parent / "socket"
            private.mkdir(mode=0o700)
            sentinel = parent / "host-only"
            sentinel.write_text("hidden", encoding="utf-8")
            state = {"requests": 0, "attempts": 0, "completed": 0,
                     "followups": 0, "model_failures": 0,
                     "rejected": 0, "writes": []}
            if task == TASKS[0]:
                answers = [{"path": FILES[task][0], "content": GOOD_API}]
            elif task == TASKS[1]:
                original = (workspace / "api/Program.cs").read_text()
                answers = [{"path": FILES[task][0],
                            "content": original.replace("values.Summ()",
                                                        "values.Sum()")}]
            else:
                answers = [{"path": FILES[task][0], "content": GOOD_SUMMARY},
                           {"path": FILES[task][1], "content": GOOD_REACT}]

            def fake_decision(model: str, digest: str, actual_task: str,
                              stage: int, done: list,
                              compile_feedback: tuple[str, ...] = ()) -> dict:
                if compile_feedback:
                    raise ValueError("Initial generator must have no feedback")
                if model != MODEL or digest != DIGEST or actual_task != task:
                    raise ValueError("Mock model identity rejected")
                if stage != len(done) or stage >= len(answers):
                    raise ValueError("Mock stage rejected")
                return answers[stage]

            with (mock.patch("pinned_openai.pinned_tags_response",
                             return_value=json.dumps(tags).encode()),
                  mock.patch("pinned_openai.pinned_show_response",
                             return_value=json.dumps(details).encode()),
                  mock.patch("pinned_qualification.verify_model_digest",
                             return_value=True),
                  mock.patch("pinned_qualification.request_model_write",
                             side_effect=fake_decision)):
                with serve_bridge(private / "ollama.sock",
                    pinned_qualification_handler(MODEL, DIGEST, task,
                                                 workspace, state)):
                    edit = docker_write(runtime, task, workspace,
                                        private, sentinel)
            if edit.returncode != 0:
                raise RuntimeError("Native OpenCode write failed for " + task +
                    ": " + edit.stderr[-400:] + " " + edit.stdout[-1500:])
            guest = json.loads(edit.stdout.strip())
            checks = guest["checks"]
            if (set(checks) != CHECKS or not all(checks.values()) or
                    guest.get("chat_requests") != len(answers) + 1):
                raise RuntimeError("Guest native write guards failed: " +
                                   json.dumps(guest)[:1400])
            if (state["requests"] != len(answers) + 1 or
                    state["attempts"] != len(answers) or
                    state["completed"] != len(answers) or
                    state["followups"] != 1 or state["rejected"] != 0):
                raise RuntimeError("Broker stage counts failed for " + task +
                                   ": " + str(state)[:500])
            scan_workspace(workspace, task)
            for answer in answers:
                file = workspace / answer["path"].removeprefix("/workspace/")
                if file.read_text(encoding="utf-8") != answer["content"]:
                    raise RuntimeError("Native write did not perform source edit")
            acceptance = docker_accept(evaluator, task, workspace, sentinel)
            if not acceptance["verified"] or not validate_result(acceptance, task):
                raise RuntimeError("Independent evaluator rejected known good")
            if sealed_snapshot(FIXTURES / task, task) != original_hash:
                raise RuntimeError("Immutable host fixture was modified")
            print("M0.16 " + task + ": NATIVE_WRITE_MOCK_CI_PASS", flush=True)
    print("M0.16 ALL_THREE_TASK_NATIVE_WRITES_MOCK_PASS")
    print("NO REAL QWEN used. Real Dell diagnostic + 3x3 qualification pending.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-image", default="codingassistant-m0-runtime:ci")
    parser.add_argument("--evaluation-image",
                        default="codingassistant-m0-qualification:ci")
    args = parser.parse_args()
    try:
        smoke(args.runtime_image, args.evaluation_image)
    except (OSError, RuntimeError, ValueError,
            subprocess.TimeoutExpired, KeyError, TypeError) as exc:
        print("M0.16 MOCK NATIVE WRITE BLOCKED:",
              str(exc)[:2000], file=sys.stderr)
        raise SystemExit(2) from None
