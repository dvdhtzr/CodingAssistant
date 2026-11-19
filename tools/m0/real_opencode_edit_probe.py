#!/usr/bin/env python3
"""M0.13 real pinned Qwen tool-call through OpenCode in disposable Podman.

Narrow pilot: one validated model-generated native edit, then a second isolated
offline C# evaluator with no inference socket. Does NOT qualify M0 or allow
general autonomous coding/shell access.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
from typing import Any

from common import ROOT, ollama_json, utcnow, write_json
from controlled_repair_pilot import (
    EVAL_CHECKS, evaluator_command, exact_checks, run_podman,
)
from disposable_workspace_probe import SOURCE, fixture_hash
from isolated_opencode_real_probe import command_for as inference_command
from ollama_bridge import serve_bridge
from openai_wire_probe import payload
from pinned_inference import verify_model_digest
from pinned_real_edit import pinned_real_edit_handler
from runtime_image_probe import IMAGE
from sandbox_probe import image_status, inspect_podman

CLIENT = pathlib.Path(__file__).with_name("isolated_real_edit.mjs")
EDIT_CHECKS = frozenset((
    "non_root", "no_new_privileges", "zero_capabilities", "read_only_root",
    "no_external_tcp", "host_sentinel_hidden", "no_git_mount",
    "unsupported_routes_denied", "wrong_model_denied",
    "cli_exited_successfully", "metadata_pinned", "two_bounded_chat_requests",
    "model_tool_call_executed", "exact_source_edit", "project_unchanged",
    "no_extra_files",
))


def edit_command(image_id: str, uid: int, gid: int,
                 private: pathlib.Path, workspace: pathlib.Path,
                 model: str, sentinel: pathlib.Path) -> list[str]:
    """Two and ONLY two mounts: private read-only socket and disposable source."""
    if (workspace.is_symlink() or not workspace.is_dir() or
            workspace.resolve() == SOURCE.resolve()):
        raise ValueError("Require independent disposable project directory")
    args = inference_command(image_id, uid, gid, private, model, sentinel)
    args.insert(args.index("--entrypoint=node"), "--volume")
    args.insert(args.index("--entrypoint=node"), f"{workspace.resolve()}:/workspace:rw")
    args[args.index("--workdir=/tmp")] = "--workdir=/workspace"
    args[args.index("--eval") + 1] = CLIENT.read_text(encoding="utf-8")
    return args


def probe(model: str, edit_timeout: int, eval_timeout: int) -> dict[str, Any]:
    report: dict[str, Any] = {
        "schema_version": 1, "stage": "M0.13", "status": "BLOCKED",
        "recorded_at_utc": utcnow(), "model": model,
        "task": "02-build-repair", "purpose": "one real native edit, no shell",
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        report["reason"] = "Requires Linux non-root Podman"
        return report
    try:
        payload(model)
        before_hash = fixture_hash(SOURCE)
        original = (SOURCE / "Program.cs").read_text(encoding="utf-8")
        if original.count("values.Summ()") != 1:
            raise ValueError("Frozen fixture changed")
        expected = original.replace("values.Summ()", "values.Sum()")
    except (ValueError, OSError) as exc:
        report["reason"] = f"Frozen fixture/model rejected: {type(exc).__name__}"
        return report
    if inspect_podman(20).get("rootless") is not True:
        report["reason"] = "Rootless Podman unavailable"
        return report
    image = image_status(IMAGE, 20)
    if not image.get("present") or not image.get("image_id"):
        report["reason"] = "Existing immutable offline runtime unavailable"
        return report
    report["image_id"] = image["image_id"]
    report["fixture_sha256"] = before_hash
    try:
        inventory = ollama_json("http://127.0.0.1:11434", "/api/tags")
        matches = [m for m in inventory.get("models", [])
                   if m.get("name") == model and m.get("digest")]
        if len(matches) != 1:
            raise ValueError("Exact installed Ollama model missing")
        digest = matches[0]["digest"]
        if not verify_model_digest(model, digest):
            raise ValueError("Pinned model digest invalid")
        report["model_digest"] = digest
    except (ValueError, OSError, RuntimeError, KeyError, TypeError) as exc:
        report["reason"] = f"Pinned provider unavailable: {type(exc).__name__}"
        return report

    with tempfile.TemporaryDirectory(prefix="ca-m013-edit-") as temp:
        root = pathlib.Path(temp)
        workspace = root / "workspace"
        shutil.copytree(SOURCE, workspace)
        if fixture_hash(workspace) != before_hash:
            report["reason"] = "Disposable initial hash changed"
            return report
        private = root / "private"
        private.mkdir(mode=0o700)
        sentinel = root / "unmounted-host-only"
        sentinel.write_text("not mounted", encoding="utf-8")
        cid_dir = root / "edit-container-id"
        cid_dir.mkdir(mode=0o700)
        state = {"requests": 0, "model_attempts": 0,
                 "model_completed": 0, "followups": 0}
        with serve_bridge(private / "ollama.sock",
                          pinned_real_edit_handler(model, digest, state)):
            try:
                args = edit_command(image["image_id"], os.getuid(), os.getgid(),
                                    private, workspace, model, sentinel)
                run = run_podman(args, cid_dir, edit_timeout)
            except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as exc:
                report["reason"] = f"Native edit stage BLOCKED: {type(exc).__name__}"
                return report
        report["edit_exit_code"] = run.returncode
        report["provider_requests"] = state["model_attempts"]
        report["provider_completed"] = state["model_completed"]
        report["chat_requests"] = state["requests"]
        report["chat_followups"] = state["followups"]
        try:
            observed = json.loads(run.stdout.strip())
            report["edit_checks"] = observed["checks"]
            report["observed_routes"] = observed.get("observed_routes", [])[:16]
        except (ValueError, TypeError, KeyError, AttributeError):
            report["reason"] = "Native edit returned no valid bounded result"
            report["stderr_tail"] = run.stderr[-240:]
            return report
        report["original_fixture_unchanged"] = fixture_hash(SOURCE) == before_hash
        report["exact_disposable_edit"] = (
            (workspace / "Program.cs").read_text(encoding="utf-8") == expected
            and (workspace / "Repair.csproj").read_bytes() ==
                (SOURCE / "Repair.csproj").read_bytes()
            and {p.name for p in workspace.iterdir()} ==
                {"Program.cs", "Repair.csproj"}
            and not any(p.is_symlink() for p in workspace.iterdir()))
        report["sentinel_untouched"] = sentinel.read_text(encoding="utf-8") == "not mounted"
        if (run.returncode != 0 or not exact_checks(observed, EDIT_CHECKS)
                or state != {"requests": 2, "model_attempts": 1,
                             "model_completed": 1, "followups": 1}
                or not report["original_fixture_unchanged"]
                or not report["exact_disposable_edit"]
                or not report["sentinel_untouched"]
                or not verify_model_digest(model, digest)):
            report["reason"] = "Native real-model edit or security evidence failed"
            report["stderr_tail"] = run.stderr[-240:]
            return report
        report["edited_source_sha256"] = hashlib.sha256(expected.encode()).hexdigest()

        eval_id = root / "evaluator-id"
        eval_id.mkdir(mode=0o700)
        try:
            cmd = evaluator_command(image["image_id"], os.getuid(), os.getgid(),
                                    workspace, sentinel)
            evaluated = run_podman(cmd, eval_id, eval_timeout)
        except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as exc:
            report["reason"] = f"Independent C# evaluator BLOCKED: {type(exc).__name__}"
            return report
        report["evaluation_exit_code"] = evaluated.returncode
        try:
            result = json.loads(evaluated.stdout.strip())
            report["evaluation_checks"] = result["checks"]
        except (ValueError, TypeError, KeyError, AttributeError):
            report["reason"] = "Independent evaluator evidence invalid"
            return report
        report["original_fixture_unchanged"] = fixture_hash(SOURCE) == before_hash
        report["sentinel_untouched"] = sentinel.read_text(encoding="utf-8") == "not mounted"
        if (evaluated.returncode != 0 or not exact_checks(result, EVAL_CHECKS)
                or not report["original_fixture_unchanged"] or
                not report["sentinel_untouched"] or
                (workspace / "Program.cs").read_text(encoding="utf-8") != expected or
                (workspace / "Repair.csproj").read_bytes() !=
                    (SOURCE / "Repair.csproj").read_bytes() or
                any(p.is_symlink() for p in workspace.rglob("*")) or
                not {p.name for p in workspace.iterdir()}.issubset(
                    {"Program.cs", "Repair.csproj", "bin", "obj"})):
            report["reason"] = "Independent build, code integrity or safety failed"
            return report
    report["status"] = "REAL_EDIT_PASS"
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default="qwen3.5:4b")
    parser.add_argument("--edit-timeout", type=int, default=420)
    parser.add_argument("--evaluation-timeout", type=int, default=240)
    parser.add_argument("--output", type=pathlib.Path,
                        default=ROOT / "artifacts/m0/real-opencode-edit.json")
    args = parser.parse_args()
    if not 300 <= args.edit_timeout <= 900 or not 120 <= args.evaluation_timeout <= 600:
        parser.error("Timeouts outside M0.13 approved budgets")
    result = probe(args.model, args.edit_timeout, args.evaluation_timeout)
    write_json(args.output, result)
    print(f'M0.13 REAL OPENCODE EDIT {result["status"]}: {args.model}')
    for category in ("edit_checks", "evaluation_checks"):
        for name, passed in sorted((result.get(category) or {}).items()):
            print(f'  {category}.{name}: {"PASS" if passed else "FAIL"}')
    for key in ("edit_exit_code", "provider_requests", "provider_completed",
                "chat_requests", "chat_followups", "evaluation_exit_code",
                "original_fixture_unchanged", "exact_disposable_edit",
                "sentinel_untouched"):
        if key in result:
            print(f'{key}: {result[key]}')
    if result.get("reason"):
        print("Reason:", result["reason"])
    if result.get("stderr_tail"):
        print("Container error (bounded):", result["stderr_tail"])
    print("Evidence:", args.output)
    print("Constrained one-edit pilot only. M0 3x3 qualification remains YELLOW.")
    return 0 if result["status"] == "REAL_EDIT_PASS" else 2


if __name__ == "__main__":
    sys.exit(main())
