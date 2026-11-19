#!/usr/bin/env python3
"""M0.17 CI: real OpenCode repair write, deterministic mock model ONLY.

Reproduce user's compiler failure from the original 6/9 run. Capture actual
CS0246 in trusted, offline evaluator; feed it into the pinned broker; have
native OpenCode write the model-mocked repaired source and verify independently
again. The model's response alone is mocked; no code is executed on host.
"""
from __future__ import annotations
import argparse
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools/m0"))
sys.path.insert(0, str(ROOT / "tests"))
from ollama_bridge import serve_bridge
from pinned_qualification import FILES, pinned_qualification_handler
from qualification_isolation_probe import FIXTURES, sealed_snapshot
from m0_qualification_native_write_smoke import (
    MODEL, DIGEST, DETAILS, docker_write, docker_accept,
)

TASK = "01-api-feature"
BAD_SOURCE = """var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/math/add", ([FromQuery] int? a, [FromQuery] int? b) =>
{
    if (a == null || b == null) return Results.BadRequest("Missing or invalid integer input");
    var sum = a + b;
    return Results.Ok(new { sum });
});
app.Run();
"""


def smoke(runtime: str, evaluator: str) -> None:
    old_hash = sealed_snapshot(FIXTURES / TASK, TASK)
    with tempfile.TemporaryDirectory(prefix="ca-m017-repair-") as temp:
        parent = pathlib.Path(temp)
        workspace = parent / "workspace"
        shutil.copytree(FIXTURES / TASK, workspace)
        file = workspace / "api/Program.cs"
        file.write_text(BAD_SOURCE, encoding="utf-8")
        sentinel = parent / "host-only"
        sentinel.write_text("hidden", encoding="utf-8")
        observed = docker_accept(evaluator, TASK, workspace, sentinel)
        feedback = tuple(observed["compiler_errors"])
        if (observed["verified"] or observed["checks"]["offline_build"] or
                not feedback or not any("CS0246" in x and "FromQuery" in x
                                        for x in feedback)):
            raise RuntimeError("Compiler diagnostics were not captured: " +
                               json.dumps(observed)[:1000])
        print("M0.17 INDEPENDENT_COMPILER_ERROR_CAPTURED", flush=True)
        # This known-good string is a deterministic CI mock answer, NOT a
        # production broker repair and NOT a fake real-model performance claim.
        answer = "using Microsoft.AspNetCore.Mvc;\n" + BAD_SOURCE
        private = parent / "private"
        private.mkdir(mode=0o700)
        tags = {"models": [{"name": MODEL, "model": MODEL,
                            "digest": DIGEST, "modified_at": "2026-11-19T00:00:00Z",
                            "size": 2000000000, "details": DETAILS}]}
        profile = {"capabilities": ["completion", "tools"], "details": DETAILS,
                   "model_info": {"qwen3.5.context_length": 32768}}
        state = {"requests": 0, "attempts": 0, "completed": 0,
                 "followups": 0, "model_failures": 0, "rejected": 0, "writes": []}

        def mocked_repair(model: str, digest: str, task: str, stage: int,
                          done: list, compile_feedback: tuple[str, ...]) -> dict:
            if (model != MODEL or digest != DIGEST or task != TASK or
                    stage != 0 or done or compile_feedback != feedback):
                raise ValueError("Untrusted compile feedback was altered")
            return {"path": FILES[TASK][0], "content": answer}

        with (mock.patch("pinned_openai.pinned_tags_response",
                         return_value=json.dumps(tags).encode()),
              mock.patch("pinned_openai.pinned_show_response",
                         return_value=json.dumps(profile).encode()),
              mock.patch("pinned_qualification.verify_model_digest",
                         return_value=True),
              mock.patch("pinned_qualification.request_model_write",
                         side_effect=mocked_repair) as model_mock):
            with serve_bridge(private / "ollama.sock",
                pinned_qualification_handler(
                    MODEL, DIGEST, TASK, workspace, state, feedback)):
                result = docker_write(runtime, TASK, workspace, private, sentinel,
                                      feedback)
        model_mock.assert_called_once()
        if result.returncode != 0:
            raise RuntimeError("Native OpenCode repair failed: " +
                               result.stderr[-300:] + result.stdout[-800:])
        guest = json.loads(result.stdout.strip())
        if (not all(guest["checks"].values()) or
                state["requests"] != 2 or state["attempts"] != 1 or
                state["completed"] != 1 or state["followups"] != 1 or
                state["rejected"] != 0 or len(state["writes"]) != 1 or
                file.read_text(encoding="utf-8") != answer):
            raise RuntimeError("Bounded native repair tool did not complete")
        print("M0.17 NATIVE_OPENCODE_REPAIR_MOCK_PASS", flush=True)
        verified = docker_accept(evaluator, TASK, workspace, sentinel)
        if (not verified["verified"] or verified["compiler_errors"] or
                sealed_snapshot(FIXTURES / TASK, TASK) != old_hash or
                sentinel.read_text(encoding="utf-8") != "hidden"):
            raise RuntimeError("Repaired source failed independent offline tests")
    print("M0.17 COMPILER_FEEDBACK_REPAIR_CI_PASS")
    print("MOCK QWEN ONLY. Dell real Qwen repair is not yet evaluated.")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--runtime", default="codingassistant-m0-runtime:ci")
    ap.add_argument("--evaluator", default="codingassistant-m0-qualification:ci")
    args = ap.parse_args()
    try:
        smoke(args.runtime, args.evaluator)
    except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired,
            KeyError, AssertionError) as exc:
        print("M0.17 REPAIR SMOKE BLOCKED:", str(exc)[:1800], file=sys.stderr)
        raise SystemExit(2) from None
