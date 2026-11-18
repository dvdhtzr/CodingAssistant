#!/usr/bin/env python3
"""M0.11 one-shot real Qwen C# repair pilot, with no autonomous model tools.

OpenCode *proposes* a strictly constrained single-file edit. A trusted host
verifies the exact one-token fix, writes only to a disposable fixture COPY,
and executes a FIXED build/test script in a second rootless no-network
container. Not an M0 3x3 model qualification run or M1 agent authorization.
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

from common import ROOT, ollama_json, utcnow, write_json
from disposable_workspace_probe import SOURCE, fixture_hash, command_for as workspace_command
from isolated_opencode_real_probe import command_for as inference_command
from ollama_bridge import serve_bridge
from openai_wire_probe import payload
from pinned_inference import verify_model_digest
from pinned_repair_proposal import frozen_prompt, pinned_repair_handler
from runtime_image_probe import IMAGE
from sandbox_probe import image_status, inspect_podman

PROPOSAL_SCRIPT = pathlib.Path(__file__).with_name("isolated_repair_proposal.mjs")
EVALUATOR_SCRIPT = pathlib.Path(__file__).with_name("isolated_repair_evaluator.mjs")
PROPOSAL_CHECKS = frozenset((
    "unprivileged", "no_new_privileges", "no_effective_capabilities",
    "root_read_only", "host_sentinel_hidden", "outbound_tcp_denied",
    "metadata_reached", "model_profile_verified", "cli_exited_successfully",
    "one_inference_request", "proposal_visible", "wrong_show_model_denied",
    "unsupported_routes_denied", "no_unexpected_cli_route",
))
EVAL_CHECKS = frozenset((
    "non_root", "no_new_privileges", "no_effective_capabilities", "root_read_only",
    "no_external_tcp", "source_is_regular", "project_is_regular",
    "offline_restore_passed", "offline_build_passed", "output_exact",
))


def proposal_command(image_id: str, uid: int, gid: int,
                     private: pathlib.Path, model: str, sentinel: pathlib.Path) -> list[str]:
    args = inference_command(image_id, uid, gid, private, model, sentinel)
    args[args.index("--eval") + 1] = PROPOSAL_SCRIPT.read_text(encoding="utf-8")
    args.append(frozen_prompt())
    return args


def evaluator_command(image_id: str, uid: int, gid: int,
                      workspace: pathlib.Path, sentinel: pathlib.Path) -> list[str]:
    args = workspace_command(image_id, uid, gid, workspace, sentinel)
    args[args.index("--eval") + 1] = EVALUATOR_SCRIPT.read_text(encoding="utf-8")
    args[args.index("--memory=512m")] = "--memory=2g"
    args[args.index("--tmpfs=/tmp:rw,nosuid,nodev,size=64m,notmpcopyup")] = (
        "--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup")
    return args


def exact_checks(response: dict[str, Any], names: frozenset[str]) -> bool:
    values = response.get("checks")
    return (type(values) is dict and set(values) == names and
            all(values[name] is True for name in names))


def run_podman(command: list[str], work: pathlib.Path, timeout: int) -> subprocess.CompletedProcess:
    """Run only immutable-ID, bounded no-network commands; force teardown on timeout."""
    cidfile = work / "container-id"
    if cidfile.exists():
        raise ValueError("Container-ID evidence already exists")
    cmd = command.copy()
    cmd.insert(cmd.index("--rm"), "--cidfile=" + str(cidfile))
    try:
        return subprocess.run(cmd, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                              stderr=subprocess.PIPE, encoding="utf-8",
                              errors="replace", text=True, check=False, timeout=timeout)
    finally:
        if cidfile.exists():
            cid = cidfile.read_text(encoding="ascii", errors="ignore").strip()
            if re.fullmatch(r"[0-9a-fA-F]{64}", cid):
                subprocess.run(["podman", "rm", "--force", cid],
                               stdin=subprocess.DEVNULL,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                               check=False, timeout=20)
            cidfile.unlink(missing_ok=True)


def probe(model: str, proposal_timeout: int, eval_timeout: int) -> dict[str, Any]:
    report: dict[str, Any] = {
        "schema_version": 1, "stage": "M0.11",
        "recorded_at_utc": utcnow(), "status": "BLOCKED",
        "model": model, "task": "02-build-repair",
        "purpose": "one validated model-authored edit; isolated fixed offline build only",
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        report["reason"] = "Requires Linux non-root Podman"
        return report
    try:
        payload(model)
        baseline = fixture_hash(SOURCE)
        original = (SOURCE / "Program.cs").read_text(encoding="utf-8")
    except (ValueError, OSError) as exc:
        report["reason"] = f"Frozen fixture/model invalid: {type(exc).__name__}"
        return report
    if inspect_podman(20).get("rootless") is not True:
        report["reason"] = "Rootless Podman unavailable"
        return report
    image = image_status(IMAGE, 20)
    if not image.get("present") or not image.get("image_id"):
        report["reason"] = "Existing verified offline runtime image unavailable"
        return report
    report["image_id"] = image["image_id"]
    report["fixture_sha256"] = baseline
    try:
        entries = ollama_json("http://127.0.0.1:11434", "/api/tags")
        matches = [m for m in entries.get("models", [])
                   if m.get("name") == model and m.get("digest")]
        if len(matches) != 1:
            raise ValueError("Exact installed model unavailable")
        digest = matches[0]["digest"]
        if not verify_model_digest(model, digest):
            raise ValueError("Model digest changed")
        report["model_digest"] = digest
    except (OSError, RuntimeError, ValueError, TypeError, KeyError) as exc:
        report["reason"] = f"Pinned provider unavailable: {type(exc).__name__}"
        return report

    state: dict[str, Any] = {"attempts": 0, "completed": 0, "candidate": None}
    with tempfile.TemporaryDirectory(prefix="ca-m011-repair-") as temp:
        parent = pathlib.Path(temp)
        private = parent / "private"
        private.mkdir(mode=0o700)
        sentinel = parent / "unmounted-host-only"
        sentinel.write_text("hidden", encoding="utf-8")
        proposal_dir = parent / "proposal-id"
        proposal_dir.mkdir(mode=0o700)
        with serve_bridge(private / "ollama.sock", pinned_repair_handler(model, digest, state)):
            try:
                cmd = proposal_command(image["image_id"], os.getuid(), os.getgid(),
                                       private, model, sentinel)
                raw = run_podman(cmd, proposal_dir, proposal_timeout)
            except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as exc:
                report["reason"] = f"OpenCode proposal stage BLOCKED: {type(exc).__name__}"
                return report
        report["proposal_exit_code"] = raw.returncode
        report["provider_requests"] = state["attempts"]
        report["provider_completed"] = state["completed"]
        try:
            summary = json.loads(raw.stdout.strip())
            report["proposal_checks"] = summary["checks"]
            report["observed_routes"] = summary.get("observed_routes", [])[:16]
            if (not exact_checks(summary, PROPOSAL_CHECKS)
                    or raw.returncode != 0):
                raise ValueError("OpenCode isolation or request checks failed")
        except (KeyError, ValueError, TypeError, AttributeError):
            report["reason"] = "OpenCode proposal check mismatch"
            return report
        if state["attempts"] != 1 or state["completed"] != 1:
            report["reason"] = "Expected exactly one completed pinned inference"
            return report
        candidate = state["candidate"]
        if type(candidate) is not str:
            report["reason"] = "No strictly validated model-authored source"
            return report
        if not verify_model_digest(model, digest):
            report["reason"] = "Model digest changed after proposal"
            return report
        report["proposal_sha256"] = hashlib.sha256(candidate.encode("utf-8")).hexdigest()

        workspace = parent / "workspace"
        shutil.copytree(SOURCE, workspace)
        if fixture_hash(workspace) != baseline:
            report["reason"] = "Disposable initial source hash differs"
            return report
        # Trusted host writes only this fixed, one-token-validated replacement.
        (workspace / "Program.cs").write_text(candidate, encoding="utf-8")
        if (workspace / "Repair.csproj").read_bytes() != (SOURCE / "Repair.csproj").read_bytes():
            report["reason"] = "Frozen project unexpectedly modified"
            return report
        eval_dir = parent / "evaluator-id"
        eval_dir.mkdir(mode=0o700)
        try:
            cmd = evaluator_command(image["image_id"], os.getuid(), os.getgid(),
                                    workspace, sentinel)
            completed = run_podman(cmd, eval_dir, eval_timeout)
        except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as exc:
            report["reason"] = f"Independent build stage BLOCKED: {type(exc).__name__}"
            return report
        report["evaluation_exit_code"] = completed.returncode
        try:
            evaluated = json.loads(completed.stdout.strip())
            report["evaluation_checks"] = evaluated["checks"]
        except (ValueError, KeyError, TypeError, AttributeError):
            report["reason"] = "Independent evaluation returned no valid bounded checks"
            return report
        report["original_fixture_unchanged"] = fixture_hash(SOURCE) == baseline
        report["only_validated_source_staged"] = (
            (workspace / "Program.cs").read_text(encoding="utf-8") == candidate
            and (workspace / "Repair.csproj").read_bytes() ==
                (SOURCE / "Repair.csproj").read_bytes()
            and not any(p.is_symlink() for p in workspace.rglob("*"))
            and set(p.name for p in workspace.iterdir()).issubset(
                {"Program.cs", "Repair.csproj", "bin", "obj"})
        )
        report["sentinel_untouched"] = sentinel.read_text(encoding="utf-8") == "hidden"
        if (completed.returncode != 0 or not exact_checks(evaluated, EVAL_CHECKS)
                or not report["original_fixture_unchanged"]
                or not report["only_validated_source_staged"]
                or not report["sentinel_untouched"]):
            report["reason"] = "Independent offline build, output or safety gate failed"
            return report
    report["status"] = "CONTROLLED_REPAIR_PASS"
    return report


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--model", default="qwen3.5:4b")
    p.add_argument("--proposal-timeout", type=int, default=420)
    p.add_argument("--evaluation-timeout", type=int, default=240)
    p.add_argument("--output", type=pathlib.Path,
                   default=ROOT / "artifacts/m0/controlled-repair.json")
    args = p.parse_args()
    if not 300 <= args.proposal_timeout <= 900 or not 120 <= args.evaluation_timeout <= 600:
        p.error("Timeout budgets outside approved ranges")
    result = probe(args.model, args.proposal_timeout, args.evaluation_timeout)
    write_json(args.output, result)
    print(f'M0.11 FIRST CONTROLLED REPAIR {result["status"]}: {args.model}')
    for group in ("proposal_checks", "evaluation_checks"):
        for key, value in sorted((result.get(group) or {}).items()):
            print(f'  {group}.{key}: {"PASS" if value else "FAIL"}')
    for item in ("proposal_exit_code", "provider_requests", "provider_completed",
                 "evaluation_exit_code", "original_fixture_unchanged",
                 "only_validated_source_staged", "sentinel_untouched"):
        if item in result:
            print(item + ":", result[item])
    if result.get("reason"):
        print("Reason:", result["reason"])
    print("Evidence:", args.output)
    print("This is a constrained repair pilot, NOT a 3x3 autonomous coding benchmark.")
    return 0 if result["status"] == "CONTROLLED_REPAIR_PASS" else 2


if __name__ == "__main__":
    sys.exit(main())
