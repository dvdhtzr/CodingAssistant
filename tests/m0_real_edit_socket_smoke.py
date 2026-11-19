#!/usr/bin/env python3
"""M0.13 CI integration smoke: real OpenCode and real broker, MOCK Qwen only.

A rootless-style nonprivileged Docker container has no network, only a
read-only private Unix broker socket and disposable C# source. Python mocks
only host Ollama metadata and the model's validated decision, never the
actual OpenCode editing tool or independent .NET evaluator.
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

from controlled_repair_pilot import EVAL_CHECKS, exact_checks
from disposable_workspace_probe import SOURCE, fixture_hash
from ollama_bridge import serve_bridge
from pinned_real_edit import pinned_real_edit_handler
from real_opencode_edit_probe import EDIT_CHECKS, CLIENT
from controlled_repair_pilot import EVALUATOR_SCRIPT

MODEL = "qwen3.5:4b"
DIGEST = "d" * 64
DETAILS = {
    "format": "gguf", "family": "qwen3.5", "families": ["qwen3.5"],
    "parameter_size": "4B", "quantization_level": "Q4_K_M",
}


def in_docker(image: str, workspace: pathlib.Path, script: str,
              private: pathlib.Path | None = None,
              sentinel: pathlib.Path | None = None) -> subprocess.CompletedProcess:
    cmd = ["docker", "run", "--rm", "--pull", "never",
           "--network", "none", "--read-only", "--cap-drop", "ALL",
           "--security-opt", "no-new-privileges", "--pids-limit", "96",
           "--memory", "2g", "--tmpfs", "/tmp:rw,nosuid,nodev,size=512m",
           "--user", f"{os.getuid()}:{os.getgid()}",
           "--volume", f"{workspace.resolve()}:/workspace:rw",
           "--env", "HOME=/tmp", "--env", "DOTNET_CLI_HOME=/tmp",
           "--workdir", "/workspace", "--entrypoint", "node"]
    if private is not None:
        cmd.extend(["--volume", f"{private.resolve()}:/run/codingassistant:ro"])
    cmd.extend([image, "--input-type=module", "--eval", script])
    if private is not None:
        cmd.extend(["--", MODEL, str(sentinel.resolve())])
    return subprocess.run(cmd, stdin=subprocess.DEVNULL, capture_output=True,
                          encoding="utf-8", text=True, errors="replace",
                          timeout=360, check=False)


def smoke(image: str) -> None:
    source_hash = fixture_hash(SOURCE)
    original = (SOURCE / "Program.cs").read_text(encoding="utf-8")
    expected = original.replace("values.Summ()", "values.Sum()")
    if original == expected or original.count("values.Summ()") != 1:
        raise RuntimeError("Frozen C# source does not match contract")
    with tempfile.TemporaryDirectory(prefix="ca-m013-ci-") as temp:
        root = pathlib.Path(temp)
        workspace = root / "workspace"
        shutil.copytree(SOURCE, workspace)
        private = root / "socket"
        private.mkdir(mode=0o700)
        sentinel = root / "host-only"
        sentinel.write_text("hidden", encoding="utf-8")
        state = {"requests": 0, "model_attempts": 0,
                 "model_completed": 0, "followups": 0}
        tags = {"models": [{"name": MODEL, "model": MODEL, "digest": DIGEST,
            "modified_at": "2026-11-18T00:00:00Z", "size": 2000000000,
            "details": DETAILS}]}
        profile = {"capabilities": ["completion", "tools"],
                   "details": DETAILS,
                   "model_info": {"qwen3.5.context_length": 32768}}
        with (mock.patch("pinned_openai.pinned_tags_response",
                         return_value=json.dumps(tags).encode()),
              mock.patch("pinned_openai.pinned_show_response",
                         return_value=json.dumps(profile).encode()),
              mock.patch("pinned_real_edit.verify_model_digest", return_value=True),
              mock.patch("pinned_real_edit.request_real_edit") as decision):
            with serve_bridge(private / "ollama.sock",
                              pinned_real_edit_handler(MODEL, DIGEST, state)):
                edit = in_docker(image, workspace,
                                 CLIENT.read_text(encoding="utf-8"),
                                 private, sentinel)
            decision.assert_called_once_with(MODEL, DIGEST)
        if edit.returncode != 0:
            raise RuntimeError("Real-CLI native edit failed: " +
                               edit.stderr[-600:] + edit.stdout[-1200:])
        guest = json.loads(edit.stdout.strip())
        if not exact_checks(guest, EDIT_CHECKS):
            raise RuntimeError("Guest native edit checks not all true")
        if state != {"requests": 2, "model_attempts": 1,
                     "model_completed": 1, "followups": 1}:
            raise RuntimeError("Unexpected broker stage counts")
        if ({p.name for p in workspace.iterdir()} !=
                {"Program.cs", "Repair.csproj"} or
                (workspace / "Program.cs").read_text() != expected or
                (workspace / "Repair.csproj").read_bytes() !=
                    (SOURCE / "Repair.csproj").read_bytes() or
                fixture_hash(SOURCE) != source_hash or
                sentinel.read_text() != "hidden"):
            raise RuntimeError("Disposable edit/source integrity violated")
        verified = in_docker(image, workspace,
                             EVALUATOR_SCRIPT.read_text(encoding="utf-8"))
        if verified.returncode != 0:
            raise RuntimeError("Independent offline evaluation failed: " +
                               verified.stderr[-600:] + verified.stdout[-1000:])
        result = json.loads(verified.stdout.strip())
        if not exact_checks(result, EVAL_CHECKS):
            raise RuntimeError("Independent offline checks not all true")
        if (fixture_hash(SOURCE) != source_hash or
                (workspace / "Program.cs").read_text() != expected or
                (workspace / "Repair.csproj").read_bytes() !=
                    (SOURCE / "Repair.csproj").read_bytes()):
            raise RuntimeError("Host fixture tampered after evaluation")
    print("M0.13 BROKER_SOCKET_OPENCODE_MOCK_PASS: native edit plus independent C# evaluator")
    print("MOCK ONLY: no local Qwen inference occurred; M0 is not qualified.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", default="codingassistant-m0-runtime:ci")
    args = parser.parse_args()
    try:
        smoke(args.image)
    except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired) as exc:
        print("M0.13 BROKER_SOCKET_OPENCODE_MOCK_BLOCKED:",
              str(exc)[:1800], file=sys.stderr)
        raise SystemExit(2) from None
