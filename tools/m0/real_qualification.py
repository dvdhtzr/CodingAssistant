#!/usr/bin/env python3
"""M0.16 real Qwen/OpenCode 3x3 qualification evidence runner on Dell.

Every attempt starts from a separate approved fixture COPY and executes
native source writes in rootless offline Podman, with one pinned local
Ollama socket. Independent verification runs in a separate no-network
container WITHOUT the inference socket. Failed model output is recorded
as FAILED, never silently repaired. Independent safety fields remain null.
The old unisolated benchmark is not invoked.
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
from typing import Any

from common import ROOT, ollama_json, sha256_file, utcnow, write_json
from controlled_repair_pilot import run_podman
from isolated_opencode_real_probe import command_for as inference_command
from ollama_bridge import serve_bridge
from openai_wire_probe import payload
from pinned_inference import verify_model_digest
from pinned_qualification import (
    FILES, pinned_qualification_handler, task_prompt,
)
from qualification_isolation_probe import (
    EXPECTED_FILES, FIXTURES, check_manifest, sealed_snapshot,
)
from qualification_acceptance_probe import (
    IMAGE as EVAL_IMAGE, run_evaluator,
)
from runtime_image_probe import IMAGE as RUNTIME_IMAGE
from sandbox_probe import image_status, inspect_podman
from qualify import TASKS, assess
from benchmark import source_hash

CLIENT = pathlib.Path(__file__).with_name("isolated_qualification_writer.mjs")
OUTPUT = ROOT / "artifacts/m0"
CHECKS = frozenset((
    "non_root", "no_new_privileges", "zero_capabilities", "root_read_only",
    "external_tcp_denied", "sentinel_hidden", "git_hidden",
    "unauthorized_socket_hidden", "unsupported_routes_denied",
    "wrong_model_denied", "metadata_pinned", "chat_bounded",
    "native_cli_complete",
))
# Never mount or operate on any host file outside the copied workspace.
ALLOW_POST_BUILD = ("api/bin/", "api/obj/")


def allowed_source_files(task: str) -> set[str]:
    return set(EXPECTED_FILES[task]) | {
        x.removeprefix("/workspace/") for x in FILES[task]}


def scan_workspace(path: pathlib.Path, task: str,
                   allow_build_outputs: bool = False) -> dict[str, str]:
    """Fail closed on any unexpected path, link, or modified project file."""
    if path.is_symlink() or not path.is_dir():
        raise ValueError("Workspace is absent or linked")
    allowed = allowed_source_files(task)
    found: dict[str, str] = {}
    for entry in path.rglob("*"):
        rel = entry.relative_to(path).as_posix()
        if entry.is_symlink():
            raise ValueError("Workspace symlink detected")
        if entry.is_dir():
            if rel in ("api", "web", "web/src"):
                continue
            if allow_build_outputs and (
                rel in ("api/bin", "api/obj") or
                rel.startswith(ALLOW_POST_BUILD)):
                continue
            raise ValueError("Unknown workspace directory")
        if not entry.is_file() or entry.stat().st_nlink != 1:
            raise ValueError("Workspace contains nonregular or linked file")
        if (rel not in allowed and
                not (allow_build_outputs and rel.startswith(ALLOW_POST_BUILD))):
            raise ValueError("Unexpected generated file outside task allowlist")
        if rel in allowed:
            data = entry.read_bytes()
            if not 0 < len(data) <= 16000:
                raise ValueError("Generated source oversized or empty")
            found[rel] = hashlib.sha256(data).hexdigest()
    if not set(EXPECTED_FILES[task]).issubset(found):
        raise ValueError("Original source file disappeared")
    return found


def qualification_command(image_id: str, uid: int, gid: int,
                          private: pathlib.Path, workspace: pathlib.Path,
                          sentinel: pathlib.Path, model: str,
                          task: str,
                          compile_feedback: tuple[str, ...] = ()) -> list[str]:
    """Only two host mounts: the private model socket and disposable copy."""
    if task not in TASKS or workspace.is_symlink() or not workspace.is_dir():
        raise ValueError("Invalid workspace")
    args = inference_command(image_id, uid, gid, private, model, sentinel)
    args.insert(args.index("--entrypoint=node"), "--volume")
    args.insert(args.index("--entrypoint=node"),
                f"{workspace.resolve()}:/workspace:rw")
    args[args.index("--workdir=/tmp")] = "--workdir=/workspace"
    args[args.index("--eval") + 1] = CLIENT.read_text(encoding="utf-8")
    args[args.index("--memory=2g")] = "--memory=3g"
    args[args.index("--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup")] = (
        "--tmpfs=/tmp:rw,nosuid,nodev,size=768m,notmpcopyup")
    if args[-3] != "--" or args[-2] != model:
        raise ValueError("Pinned OpenCode command changed")
    prompt = task_prompt(task)
    if compile_feedback:
        if task not in ("01-api-feature", "02-build-repair"):
            raise ValueError("Repair only supported for bounded single-file tasks")
        prompt += ("\\nA separate, offline .NET build failed with: " +
                   json.dumps(compile_feedback) +
                   "\\nRepair the original task in the same disposable source file.")
    args[-1:] = [task, prompt, str(sentinel.resolve())]
    return args


def pin_model(model: str) -> str:
    payload(model)
    inventory = ollama_json("http://127.0.0.1:11434", "/api/tags")
    matches = [m["digest"] for m in inventory.get("models", [])
               if m.get("name") == model and m.get("digest")]
    if len(matches) != 1 or not verify_model_digest(model, matches[0]):
        raise ValueError("Exact installed Ollama tag/digest unavailable")
    return matches[0]


def preflight(model: str, image_name: str = EVAL_IMAGE) -> dict[str, Any]:
    if sys.platform != "linux" or os.geteuid() == 0:
        raise RuntimeError("Must use Linux non-root rootless Podman")
    if inspect_podman(20).get("rootless") is not True:
        raise RuntimeError("Rootless Podman unavailable")
    manifest_sha = check_manifest()
    baselines = {task: sealed_snapshot(FIXTURES / task, task)
                 for task in TASKS}
    base = image_status(RUNTIME_IMAGE, 20)
    eval_image = image_status(image_name, 20)
    if (not base.get("present") or not base.get("image_id") or
            not eval_image.get("present") or not eval_image.get("image_id")):
        raise RuntimeError("Immutable runtime and/or qualification images missing")
    digest = pin_model(model)
    return {"manifest_sha256": manifest_sha,
            "fixture_sha256": baselines,
            "runtime_image_id": base["image_id"],
            "evaluator_image_id": eval_image["image_id"],
            "model_digest": digest}


def provision_evaluator() -> dict[str, Any]:
    """EXPLICIT trusted image build; never invoked by the model runner."""
    if sys.platform != "linux" or os.geteuid() == 0:
        raise RuntimeError("Build evaluator as normal Linux user only")
    if inspect_podman(20).get("rootless") is not True:
        raise RuntimeError("Rootless Podman unavailable")
    base = image_status(RUNTIME_IMAGE, 20)
    if not base.get("present") or not base.get("image_id"):
        raise RuntimeError("M0.7 runtime image not present")
    image_file = ROOT / "infra/qualification/Containerfile"
    if not image_file.is_file():
        raise RuntimeError("Trusted qualification recipe absent")
    # Dependencies may download only at this owner-requested provisioning
    # step. Runtime evaluations and all model calls are offline/local.
    command = ["podman", "build", "--pull=never",
               "--file", str(image_file), "--tag", EVAL_IMAGE, str(ROOT)]
    completed = subprocess.run(
        command, stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        encoding="utf-8", errors="replace", timeout=1200, check=False)
    if completed.returncode != 0:
        raise RuntimeError("Qualification image build failed: " +
                           completed.stderr[-400:])
    result = image_status(EVAL_IMAGE, 20)
    if not result.get("present") or not result.get("image_id"):
        raise RuntimeError("Built image lacks immutable inspected ID")
    return {"status": "PROVISIONED", "runtime_image_id": base["image_id"],
            "evaluator_image_id": result["image_id"]}


def attempt_compile_repair(
        task: str, model: str, context: dict[str, Any],
        parent: pathlib.Path, workspace: pathlib.Path,
        sentinel: pathlib.Path, compiler_errors: tuple[str, ...],
        agent_timeout: int) -> tuple[dict[str, Any], bool]:
    """One extra model-authored native write, never shell or host build.

    A repair is part of the SAME benchmark attempt and is possible only
    after a failed independent C# compilation and validated diagnostics.
    Returns (bounded evidence, fatal_security_or_infra_failure).
    """
    repair: dict[str, Any] = {
        "attempted": True, "compiler_errors": list(compiler_errors),
        "status": "MODEL_FAILED",
    }
    if task not in ("01-api-feature", "02-build-repair"):
        raise ValueError("Only single-file C# repair is currently supported")
    socket_dir = parent / "repair-socket"
    socket_dir.mkdir(mode=0o700)
    container_id = parent / "repair-container-id"
    container_id.mkdir(mode=0o700)
    state: dict[str, Any] = {
        "requests": 0, "attempts": 0, "completed": 0,
        "followups": 0, "model_failures": 0, "rejected": 0, "writes": [],
    }
    try:
        with serve_bridge(socket_dir / "ollama.sock",
                pinned_qualification_handler(
                    model, context["model_digest"], task,
                    workspace, state, compiler_errors)):
            cmd = qualification_command(
                context["runtime_image_id"], os.getuid(), os.getgid(),
                socket_dir, workspace, sentinel, model, task,
                compiler_errors)
            completed = run_podman(cmd, container_id, agent_timeout)
    except (OSError, RuntimeError, ValueError,
            subprocess.TimeoutExpired) as exc:
        repair["status"] = "ENVIRONMENT_BLOCKED"
        repair["reason"] = type(exc).__name__
        return repair, True
    repair["opencode_exit_code"] = completed.returncode
    repair["requests"] = state["requests"]
    repair["model_attempts"] = state["attempts"]
    repair["model_completed"] = state["completed"]
    repair["model_failures"] = state["model_failures"]
    repair["route_rejections"] = state["rejected"]
    repair["writes"] = [{
        "path": item["path"],
        "bytes": len(item["content"].encode("utf-8")),
        "sha256": hashlib.sha256(item["content"].encode("utf-8")).hexdigest()
    } for item in state["writes"]]
    try:
        data = json.loads(completed.stdout.strip())
        guards = data["checks"]
        if (data.get("task") != task or type(guards) is not dict or
                set(guards) != CHECKS or
                any(type(v) is not bool for v in guards.values())):
            raise ValueError("Invalid guest evidence")
    except (ValueError, TypeError, KeyError, AttributeError):
        repair["status"] = "ENVIRONMENT_BLOCKED"
        repair["reason"] = "INVALID_GUEST_EVIDENCE"
        return repair, True
    repair["checks"] = guards
    security = CHECKS - {"metadata_pinned", "chat_bounded", "native_cli_complete"}
    if (any(not guards[key] for key in security) or
            sentinel.read_text(encoding="utf-8") != "not mounted"):
        repair["status"] = "SECURITY_BLOCKED"
        return repair, True
    try:
        sources = scan_workspace(workspace, task, allow_build_outputs=True)
        repair["source_sha256"] = sources
        if (sealed_snapshot(FIXTURES / task, task) !=
                context["fixture_sha256"][task]):
            raise ValueError("Frozen baseline changed")
    except (OSError, ValueError):
        repair["status"] = "SECURITY_BLOCKED"
        repair["reason"] = "WORKSPACE_OR_ORIGINAL_INTEGRITY"
        return repair, True
    target = FILES[task][0]
    repair["completed"] = (
        completed.returncode == 0 and all(guards.values()) and
        state["requests"] == 2 and state["attempts"] == 1 and
        state["completed"] == 1 and state["followups"] == 1 and
        state["rejected"] == 0 and state["model_failures"] == 0 and
        len(state["writes"]) == 1 and
        state["writes"][0]["path"] == target and
        verify_model_digest(model, context["model_digest"]))
    repair["status"] = "MODEL_WRITE_COMPLETE" if repair["completed"] else "MODEL_FAILED"
    return repair, False


def eval_case(task: str, repeat: int, model: str, context: dict[str, Any],
              root: pathlib.Path, agent_timeout: int,
              eval_timeout: int) -> tuple[dict[str, Any], bool]:
    """Return row and whether the whole batch must stop for safety/infrastructure."""
    name = f"{task}-{repeat}"
    work = root / "workspaces" / name
    shutil.copytree(FIXTURES / task, work)
    start_hash = sealed_snapshot(work, task)
    original_hash = context["fixture_sha256"][task]
    if start_hash != original_hash:
        raise RuntimeError("New workspace differs from frozen baseline")
    record: dict[str, Any] = {
        "model": model, "model_digest": context["model_digest"],
        "task": task, "repeat": repeat,
        "workspace": str(work), "started_from_hash": start_hash,
        "verified": False, "unauthorized_operation": None,
        "false_verification_claim": None,
        "unrecoverable_execution_failure": None,
        "status": "MODEL_FAILED", "observed_at": utcnow(),
    }
    start_at = time.monotonic()
    with tempfile.TemporaryDirectory(prefix="ca-m016-private-") as temp:
        parent = pathlib.Path(temp)
        private = parent / "socket"
        private.mkdir(mode=0o700)
        sentinel = parent / "not-mounted"
        sentinel.write_text("not mounted", encoding="utf-8")
        state: dict[str, Any] = {
            "requests": 0, "attempts": 0, "completed": 0,
            "followups": 0, "model_failures": 0,
            "rejected": 0, "writes": [],
        }
        cid_dir = parent / "writer-id"
        cid_dir.mkdir(mode=0o700)
        try:
            with serve_bridge(private / "ollama.sock",
                    pinned_qualification_handler(
                        model, context["model_digest"], task, work, state)):
                command = qualification_command(
                    context["runtime_image_id"], os.getuid(), os.getgid(),
                    private, work, sentinel, model, task)
                result = run_podman(command, cid_dir, agent_timeout)
        except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as exc:
            record["reason"] = "MODEL_CONTAINER_EXCEPTION_" + type(exc).__name__
            record["status"] = "ENVIRONMENT_BLOCKED"
            return record, True
        record["opencode_exit_code"] = result.returncode
        record["model_requests"] = state["attempts"]
        record["model_completions"] = state["completed"]
        record["chat_requests"] = state["requests"]
        record["model_rejections"] = state["model_failures"]
        record["route_rejections"] = state["rejected"]
        record["write_evidence"] = [{
            "path": x["path"],
            "sha256": hashlib.sha256(x["content"].encode("utf-8")).hexdigest(),
            "bytes": len(x["content"].encode("utf-8"))}
            for x in state["writes"]]
        try:
            guest = json.loads(result.stdout.strip())
            guards = guest["checks"]
            if (guest.get("task") != task or
                    type(guards) is not dict or set(guards) != CHECKS or
                    any(type(v) is not bool for v in guards.values())):
                raise ValueError("Unexpected guest safety result")
            record["agent_checks"] = guards
            record["agent_trace"] = {
                "exit": guest.get("cli", {}).get("exit"),
                "completed": guest.get("cli", {}).get("complete"),
                "stdout_tail": str(guest.get("cli", {}).get("stdoutTail", ""))[:2000],
                "stderr_tail": str(guest.get("cli", {}).get("stderrTail", ""))[:500],
            }
        except (ValueError, AttributeError, KeyError, TypeError):
            record["reason"] = "INVALID_GUEST_SAFETY_EVIDENCE"
            record["status"] = "ENVIRONMENT_BLOCKED"
            return record, True
        security_keys = CHECKS - {"metadata_pinned", "chat_bounded", "native_cli_complete"}
        if (any(not guards[k] for k in security_keys) or
                sentinel.read_text(encoding="utf-8") != "not mounted"):
            record["reason"] = "OS_ISOLATION_GUARD_FAILED"
            record["status"] = "SECURITY_BLOCKED"
            return record, True
        try:
            observed = scan_workspace(work, task)
            record["finished_at_hash"] = source_hash(work)
            record["source_files_sha256"] = observed
            if sealed_snapshot(FIXTURES / task, task) != original_hash:
                raise ValueError("Original frozen source changed")
        except (ValueError, OSError) as exc:
            record["reason"] = "WORKSPACE_INTEGRITY_" + type(exc).__name__
            record["status"] = "SECURITY_BLOCKED"
            return record, True
        expected = set(FILES[task])
        actual = {x["path"] for x in state["writes"]}
        model_complete = (
            result.returncode == 0 and all(guards.values()) and
            state["attempts"] == len(expected) and
            state["completed"] == len(expected) and
            state["followups"] == 1 and state["rejected"] == 0 and
            actual == expected and len(state["writes"]) == len(expected) and
            verify_model_digest(model, context["model_digest"]))
        record["model_completed"] = model_complete
        # The evaluator runs even if the model failed, so its failure is
        # recorded honestly. It runs in a DIFFERENT container, without a
        # model socket. Only bounded fixed commands are executed.
        try:
            evaluator = run_evaluator(
                context["evaluator_image_id"], task, work, sentinel,
                parent / "evaluator-id", timeout=eval_timeout)
        except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as exc:
            record["reason"] = "INDEPENDENT_EVALUATOR_" + type(exc).__name__
            record["status"] = "ENVIRONMENT_BLOCKED"
            return record, True
        record["acceptance"] = evaluator
        record["verified"] = model_complete and evaluator["verified"] is True
        if evaluator["status"] in ("SECURITY_BLOCKED", "INVALID_EVIDENCE",
                                    "ENVIRONMENT_BLOCKED", "INVALID_EXIT_CONTRACT"):
            record["status"] = evaluator["status"]
            return record, True
        if sealed_snapshot(FIXTURES / task, task) != original_hash:
            record["reason"] = "FROZEN_SOURCE_TAMPERED_AFTER_EVALUATION"
            record["status"] = "SECURITY_BLOCKED"
            return record, True
        try:
            post = scan_workspace(work, task, allow_build_outputs=True)
            if post != observed:
                raise ValueError("Source modified during acceptance")
        except (ValueError, OSError):
            record["reason"] = "INDEPENDENT_EVALUATOR_CHANGED_SOURCE"
            record["status"] = "SECURITY_BLOCKED"
            return record, True
        # A compile-error retry is NOT another independent benchmark run.
        # It is a single, bounded correction within this original attempt.
        # Never attempt retry on security failure, transport failure, or an
        # unverified initial OpenCode source write.
        repair_allowed = (
            model_complete and not evaluator["verified"] and
            task in ("01-api-feature", "02-build-repair") and
            evaluator["checks"]["offline_restore"] is True and
            evaluator["checks"]["offline_build"] is False and
            bool(evaluator["compiler_errors"]))
        record["repair_attempted"] = repair_allowed
        if repair_allowed:
            feedback = tuple(evaluator["compiler_errors"])
            record["initial_acceptance"] = evaluator
            repair, fatal = attempt_compile_repair(
                task, model, context, parent, work, sentinel,
                feedback, agent_timeout)
            record["repair"] = repair
            if fatal:
                record["status"] = repair["status"]
                record["reason"] = repair.get("reason", repair["status"])
                return record, True
            # The independent verifier's first build may have written bin/obj.
            # Snapshot only approved source files before and after every
            # subsequent verification; no generated source may be modified
            # by the independent evaluator.
            observed = repair["source_sha256"]
            try:
                second = run_evaluator(
                    context["evaluator_image_id"], task, work, sentinel,
                    parent / "repaired-evaluator-id", timeout=eval_timeout)
            except (OSError, RuntimeError, ValueError,
                    subprocess.TimeoutExpired) as exc:
                record["reason"] = "REPAIR_EVALUATOR_" + type(exc).__name__
                record["status"] = "ENVIRONMENT_BLOCKED"
                return record, True
            record["acceptance"] = second
            repair["acceptance_status"] = second["status"]
            if second["status"] in ("SECURITY_BLOCKED", "INVALID_EVIDENCE",
                                    "ENVIRONMENT_BLOCKED", "INVALID_EXIT_CONTRACT"):
                record["status"] = second["status"]
                return record, True
            if sealed_snapshot(FIXTURES / task, task) != original_hash:
                record["reason"] = "FROZEN_SOURCE_TAMPERED_AFTER_REPAIR"
                record["status"] = "SECURITY_BLOCKED"
                return record, True
            try:
                if scan_workspace(work, task, allow_build_outputs=True) != observed:
                    raise ValueError("Evaluator modified repaired source")
            except (ValueError, OSError):
                record["reason"] = "REPAIR_EVALUATOR_CHANGED_SOURCE"
                record["status"] = "SECURITY_BLOCKED"
                return record, True
            record["source_files_sha256"] = observed
            record["finished_at_hash"] = source_hash(work)
            record["verified"] = (model_complete and
                                  repair.get("completed") is True and
                                  second["verified"] is True)
        record["status"] = "VERIFIED" if record["verified"] else "MODEL_FAILED"
        record["duration_seconds"] = round(time.monotonic() - start_at, 2)
        return record, False


def run(model: str, agent_timeout: int, eval_timeout: int,
        diagnostic_task: str | None = None) -> dict[str, Any]:
    context = preflight(model)
    tag = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    root = OUTPUT / f"evaluation-{tag}-{uuid.uuid4().hex[:8]}"
    root.mkdir(parents=True, exist_ok=False)
    (root / "workspaces").mkdir(mode=0o700)
    expected = [(diagnostic_task, 1)] if diagnostic_task else [
        (task, repeat) for task in TASKS for repeat in (1, 2, 3)]
    report: dict[str, Any] = {
        "schema_version": 1, "stage": "M0.16-real-qualification",
        "status": "IN_PROGRESS", "created_utc": utcnow(),
        "model": model, "model_digest": context["model_digest"],
        "runtime_image_id": context["runtime_image_id"],
        "evaluator_image_id": context["evaluator_image_id"],
        "task_manifest_sha256": context["manifest_sha256"],
        "task_manifest": str(ROOT / "benchmarks/tasks.json"),
        "fixture_sha256": context["fixture_sha256"],
        "diagnostic": diagnostic_task is not None,
        "runs": [], "review_status": "INDEPENDENT_REVIEW_PENDING",
        "note": "This is raw model evidence. Safety fields intentionally null.",
    }
    path = root / "results.json"
    write_json(path, report)
    for task, repeat in expected:
        row, fatal = eval_case(
            task, repeat, model, context, root, agent_timeout, eval_timeout)
        report["runs"].append(row)
        write_json(path, report)
        print(f'M0.16 {task}/{repeat}: {row["status"]}', flush=True)
        if fatal:
            report["status"] = "BLOCKED"
            report["reason"] = row.get("reason", row["status"])
            break
        if not verify_model_digest(model, context["model_digest"]):
            report["status"] = "BLOCKED"
            report["reason"] = "MODEL_DIGEST_DRIFT"
            break
    else:
        report["status"] = ("DIAGNOSTIC_COMPLETE" if diagnostic_task
                            else "EVIDENCE_COMPLETE_REVIEW_PENDING")
    report["completed_utc"] = utcnow()
    report["qualification"] = assess(report["runs"], model)
    write_json(path, report)
    print("Evidence:", path)
    print("Verified functional runs:",
          sum(row["verified"] for row in report["runs"]), "/", len(expected))
    print("Qualification:", report["qualification"]["status"],
          "(independent safety review still required)")
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default="qwen3.5:4b")
    parser.add_argument("--prepare-evaluator", action="store_true",
                        help="Explicitly build the trusted offline acceptance image")
    parser.add_argument("--diagnostic-task", choices=TASKS,
                        help="Run one real Qwen task as a diagnostic, never a qualification")
    parser.add_argument("--agent-timeout", type=int, default=840)
    parser.add_argument("--evaluation-timeout", type=int, default=360)
    args = parser.parse_args()
    if not 360 <= args.agent_timeout <= 1200:
        parser.error("Agent timeout must be 360..1200 seconds")
    if not 120 <= args.evaluation_timeout <= 600:
        parser.error("Evaluation timeout must be 120..600 seconds")
    try:
        if args.prepare_evaluator:
            status = provision_evaluator()
            print("M0.16 EVALUATOR_IMAGE_PROVISIONED")
            print("Runtime image ID:", status["runtime_image_id"])
            print("Evaluator image ID:", status["evaluator_image_id"])
            print("No model or generated code executed during provisioning.")
            return 0
        report = run(args.model, args.agent_timeout, args.evaluation_timeout,
                     args.diagnostic_task)
    except (RuntimeError, ValueError, OSError, subprocess.TimeoutExpired) as exc:
        print("M0.16 BLOCKED:", type(exc).__name__, str(exc)[:300], file=sys.stderr)
        return 2
    return 0 if report["status"] in (
        "DIAGNOSTIC_COMPLETE", "EVIDENCE_COMPLETE_REVIEW_PENDING") else 2


if __name__ == "__main__":
    sys.exit(main())
