#!/usr/bin/env python3
"""M0.18 independent, offline, fail-closed review of completed M0.16 evidence.

NO Ollama, model agent or unisolated generated-code execution. Re-executes
only the TRUSTED independent acceptance evaluation in fresh, no-network
containers against disposable copies. NEVER mutates original evidence or workspaces.
Unlike the agent runner, this does not import its model broker/source scanner.
Historical approved source/fixture Git blob IDs are frozen here.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import re
import stat
import sys
import shutil
import tempfile
import subprocess
import uuid
from collections.abc import Callable
from typing import Any

from common import ROOT, utcnow, write_json
from qualify import TASKS, assess
from qualification_acceptance_probe import run_evaluator

FROZEN_INPUT_BLOBS = {
    "benchmarks/tasks.json": "44f5937e3956d9fd6fb8400bb0c585ce00407372",
    "benchmarks/fixtures/01-api-feature/api/Program.cs": "14d0787d24d2ff54b770037931df2399e27a9237",
    "benchmarks/fixtures/01-api-feature/api/Api.csproj": "4c69387cc90f6fd33aab2d631e485536d2bee6bd",
    "benchmarks/fixtures/02-build-repair/api/Program.cs": "2cc8a3cd1b829ab9b807c62a8d562e1b955efbbb",
    "benchmarks/fixtures/02-build-repair/api/Repair.csproj": "238f157e2e4d1865e290386b8f156d70d29c8f42",
    "benchmarks/fixtures/03-fullstack-feature/api/Program.cs": "14d0787d24d2ff54b770037931df2399e27a9237",
    "benchmarks/fixtures/03-fullstack-feature/api/Api.csproj": "4c69387cc90f6fd33aab2d631e485536d2bee6bd",
    "benchmarks/fixtures/03-fullstack-feature/web/package.json": "c1aae443ca11c7edf5d4f8af21e533b78a036d93",
    "benchmarks/fixtures/03-fullstack-feature/web/src/App.tsx": "3343632ec51b7c60b75f0e151cab6d99c6817e53",
    "benchmarks/fixtures/03-fullstack-feature/web/tsconfig.json": "545e12ec61272f9f6357a2f0fb31bd0bb164a941",
    "benchmarks/acceptance/TaskSummary.acceptance.test.tsx":
        "64e0b1a239cd9f8b00880ccc5dccb6dcf14119d7",
}
# The tested, approved agent and evaluator implementation that actually
# produced the owner's 9/9 evidence. Do not silently reinterpret results
# from a different code revision under these trust assumptions.
RUN_IMPL_BLOBS = {
    "tools/m0/pinned_qualification.py": "b23f49e386d411ef034714b2d31deb3e1d9bc38c",
    "tools/m0/isolated_qualification_writer.mjs": "b88a4a6ea92ccb393e811ed12202e262bf2469f5",
    "tools/m0/real_qualification.py": "34a503093c564f10be7be990ae1f9d0ecee868d1",
    "tools/m0/qualification_acceptance_client.mjs":
        "872de244f586aabde359920d81c2b68ba931c89e",
    "tools/m0/qualification_acceptance_probe.py":
        "677e3d9595164103b7bbeb6cffb4b74a42fb3d92",
    "tools/m0/ollama_bridge.py": "4212d1e61775a8b9ad25302552b6fe381109705e",
    "tools/m0/pinned_openai.py": "513c3df006f9e1aef7bd54ea68fc3b0e53636976",
    "tools/m0/pinned_inference.py": "3da99516ccea9ac9a8f2cee4d2eaba70b9eafa32",
    "tools/m0/controlled_repair_pilot.py":
        "856c75cd71a00e4de33c827a9f49fe9316ba71f6",
    "tools/m0/qualify.py": "7d11f76c617632142bd579aeeb116339935f92c0",
}
ORIGINAL_FILES = {
    "01-api-feature": {"api/Api.csproj", "api/Program.cs"},
    "02-build-repair": {"api/Repair.csproj", "api/Program.cs"},
    "03-fullstack-feature": {
        "api/Api.csproj", "api/Program.cs", "web/package.json",
        "web/src/App.tsx", "web/tsconfig.json",
    },
}
MODEL_WRITES = {
    "01-api-feature": ("/workspace/api/Program.cs",),
    "02-build-repair": ("/workspace/api/Program.cs",),
    "03-fullstack-feature": (
        "/workspace/api/Program.cs", "/workspace/web/src/TaskSummary.tsx",
    ),
}
GUEST_CHECKS = frozenset((
    "non_root", "no_new_privileges", "zero_capabilities", "root_read_only",
    "external_tcp_denied", "sentinel_hidden", "git_hidden",
    "unauthorized_socket_hidden", "unsupported_routes_denied",
    "wrong_model_denied", "metadata_pinned", "chat_bounded",
    "native_cli_complete",
))
EVAL_GUARDS = frozenset((
    "non_root", "no_new_privileges", "zero_capabilities", "root_read_only",
    "external_tcp_denied", "host_sentinel_hidden", "host_git_hidden",
    "inference_socket_hidden", "original_sources_regular",
    "trusted_tests_available", "network_not_required",
))
EVAL_CHECKS = frozenset((
    "offline_restore", "offline_build", "health", "feature_behavior",
    "frontend_typecheck", "frontend_behavior", "injected_test_cleaned",
    "offline_node_dependencies",
))
HEX64 = re.compile(r"[0-9a-f]{64}")
HEX40 = re.compile(r"[0-9a-f]{40}")
CSDIAG = re.compile(r"CS[0-9]{4}: [ -~]{1,200}")
MAX_SOURCE_BYTES = 16000


def must(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def plain_file(file: pathlib.Path, limit: int = 4_000_000) -> bytes:
    st = file.lstat()
    must(stat.S_ISREG(st.st_mode) and st.st_nlink == 1,
         "Untrusted symlink/hardlink/nonregular file")
    must(0 < st.st_size <= limit, "Untrusted file size")
    return file.read_bytes()


def blob_id(data: bytes) -> str:
    return hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()


def trusted_code_and_fixtures() -> None:
    for name, sha in {**FROZEN_INPUT_BLOBS, **RUN_IMPL_BLOBS}.items():
        must(blob_id(plain_file(ROOT / name)) == sha,
             "Approved frozen source/tool implementation drift: " + name)


def fixture_hash(task: str) -> str:
    """Independent implementation of the original frozen fixture digest."""
    root = ROOT / "benchmarks/fixtures" / task
    must(root.is_dir() and not root.is_symlink(), "Frozen source is linked")
    digest = hashlib.sha256()
    found = set()
    for item in sorted(root.rglob("*")):
        must(not item.is_symlink(), "Frozen source contains symlink")
        if item.is_dir():
            continue
        rel = item.relative_to(root).as_posix()
        data = plain_file(item, 1024 * 1024)
        found.add(rel)
        digest.update(rel.encode() + b"\0" + data + b"\0")
    must(found == ORIGINAL_FILES[task], "Frozen source set changed")
    return digest.hexdigest()


def current_source_hash(workspace: pathlib.Path, task: str) -> tuple[dict[str, str], str]:
    """Reconstruct source-hash map and previous runner's source_hash, without
    importing runner code. Reject unknown source paths and all links.
    Build binaries/obj are tolerated but never opened/executed or trusted.
    """
    must(workspace.is_dir() and not workspace.is_symlink(), "Workspace linked/missing")
    found: dict[str, str] = {}
    names: set[str] = set()
    digest = hashlib.sha256()
    allowed = ORIGINAL_FILES[task] | {
        p.removeprefix("/workspace/") for p in MODEL_WRITES[task]
    }
    for item in sorted(workspace.rglob("*")):
        rel = item.relative_to(workspace).as_posix()
        must(not item.is_symlink(), "Workspace contains symlink")
        if item.is_dir():
            must(rel in ("api", "web", "web/src", "api/bin", "api/obj") or
                 rel.startswith(("api/bin/", "api/obj/")),
                 "Unapproved generated directory")
            continue
        must(item.is_file(), "Nonregular workspace entry")
        if rel.startswith(("api/bin/", "api/obj/")):
            # Allowed products of the fixed evaluator; never read.
            continue
        must(rel in allowed, "Unapproved source/tool file: " + rel)
        data = plain_file(item, MAX_SOURCE_BYTES)
        found[rel] = hashlib.sha256(data).hexdigest()
        names.add(rel)
        # Original benchmark.source_hash algorithm:
        digest.update(rel.encode())
        digest.update(found[rel].encode())
    must(ORIGINAL_FILES[task].issubset(names), "Original source missing")
    must(all(rel in names for rel in (MODEL_WRITES[task][0].removeprefix("/workspace/"),)),
         "Model output missing")
    return found, digest.hexdigest()


def exact_true(value: object, names: frozenset[str]) -> bool:
    return type(value) is dict and set(value) == names and all(
        value[k] is True for k in names)


def sha256(value: object) -> bool:
    return type(value) is str and bool(HEX64.fullmatch(value))


def check_acceptance(obj: object, *, successful: bool) -> None:
    must(type(obj) is dict, "Evaluator record absent")
    must(exact_true(obj.get("guards"), EVAL_GUARDS),
         "Independent evaluator OS guard missing or false")
    checks = obj.get("checks")
    must(type(checks) is dict and set(checks) == EVAL_CHECKS and
         all(type(value) is bool for value in checks.values()),
         "Malformed evaluator behavior checks")
    errors = obj.get("compiler_errors")
    must(type(errors) is list and len(errors) <= 5 and
         all(type(e) is str and len(e) <= 208 and CSDIAG.fullmatch(e)
             for e in errors), "Malformed compiler feedback")
    if successful:
        must(obj.get("status") == "VERIFIED" and obj.get("verified") is True and
             obj.get("exit_code") == 0 and all(checks.values()) and not errors,
             "Independent final acceptance not verified")
    else:
        must(obj.get("status") == "ACCEPTANCE_FAILED" and
             obj.get("verified") is False and obj.get("exit_code") == 2 and
             checks["offline_restore"] is True and
             checks["offline_build"] is False and len(errors) > 0,
             "Repair must originate only from a real, failed C# build")


def check_writes(writes: object, task: str, current: dict[str, str],
                 *, repair: bool) -> None:
    paths = set(MODEL_WRITES[task]) if not repair else {MODEL_WRITES[task][0]}
    must(type(writes) is list and len(writes) == len(paths),
         "Unexpected number of model-native writes")
    must({x.get("path") for x in writes if type(x) is dict} == paths,
         "Model wrote unauthorized path or repeated path")
    for entry in writes:
        must(type(entry) is dict and sha256(entry.get("sha256")) and
             type(entry.get("bytes")) is int and
             0 < entry["bytes"] <= MAX_SOURCE_BYTES,
             "Native model write metadata invalid")
        must(entry["sha256"] == current[entry["path"].removeprefix("/workspace/")],
             "Recorded model native write differs from final source")


def inspect_run(row: dict[str, Any], report: dict[str, Any],
                case: tuple[str, int], root: pathlib.Path) -> dict[str, Any]:
    task, repeat = case
    must(type(row) is dict and row.get("task") == task and
         row.get("repeat") == repeat, "Unknown or duplicate benchmark identity")
    must(row.get("model") == report["model"] and
         row.get("model_digest") == report["model_digest"],
         "Model provenance mismatch")
    must(row.get("started_from_hash") == report["fixture_sha256"][task],
         "Copied initial source does not match sealed original")
    must(row.get("status") == "VERIFIED" and row.get("verified") is True and
         row.get("model_completed") is True and
         row.get("opencode_exit_code") == 0,
         "Benchmark attempt not verified")
    must(row.get("unauthorized_operation") is None and
         row.get("false_verification_claim") is None and
         row.get("unrecoverable_execution_failure") is None,
         "Historical safety fields were altered")
    n = len(MODEL_WRITES[task])
    must(row.get("model_requests") == n and
         row.get("model_completions") == n and
         row.get("chat_requests") == n + 1 and
         row.get("model_rejections") == 0 and
         row.get("route_rejections") == 0,
         "Model/tool request budget or rejection evidence inconsistent")
    must(exact_true(row.get("agent_checks"), GUEST_CHECKS),
         "Model execution guest OS guard missing or false")
    trace = row.get("agent_trace")
    must(type(trace) is dict and trace.get("exit") == 0 and
         trace.get("completed") is True, "Native OpenCode process not complete")
    # Model's only permitted model output is tool-call arguments. The
    # completed session's last text is an audited, fixed broker marker;
    # it explicitly does NOT claim generated source was verified.
    stdout = trace.get("stdout_tail")
    stderr = trace.get("stderr_tail")
    must(type(stdout) is str and
         stdout.strip() == "SOURCE WRITES COMPLETE; NOT VERIFIED",
         "Unexpected model/terminal verification claim")
    must(type(stderr) is str and len(stderr) <= 500,
         "Unbounded agent terminal output")
    expected = root / "workspaces" / f"{task}-{repeat}"
    must(row.get("workspace") == str(expected) and expected.exists() and
         not expected.is_symlink(), "Benchmark workspace relocated/linked")
    source_map, hash_current = current_source_hash(expected, task)
    must(row.get("source_files_sha256") == source_map and
         row.get("finished_at_hash") == hash_current,
         "Workspace source or output hash differs from report")
    # Immutable, pre-approved fixture sources must remain unchanged.
    for rel in ORIGINAL_FILES[task] - {
        p.removeprefix("/workspace/") for p in MODEL_WRITES[task]
    }:
        raw = plain_file(expected / rel, MAX_SOURCE_BYTES)
        must(blob_id(raw) == FROZEN_INPUT_BLOBS[
            f"benchmarks/fixtures/{task}/{rel}"],
            "Non-allowlisted frozen source changed")
    check_acceptance(row.get("acceptance"), successful=True)
    repaired = row.get("repair_attempted") is True
    if repaired:
        must(task != "03-fullstack-feature",
             "Two-file fullstack repair not approved")
        initial = row.get("initial_acceptance")
        check_acceptance(initial, successful=False)
        must(row.get("repair", {}).get("compiler_errors") ==
             initial["compiler_errors"], "Compiler feedback substituted")
        r = row["repair"]
        must(r.get("attempted") is True and r.get("completed") is True and
             r.get("status") == "MODEL_WRITE_COMPLETE" and
             r.get("opencode_exit_code") == 0 and
             r.get("requests") == 2 and r.get("model_attempts") == 1 and
             r.get("model_completed") == 1 and
             r.get("model_failures") == 0 and
             r.get("route_rejections") == 0 and
             r.get("acceptance_status") == "VERIFIED" and
             exact_true(r.get("checks"), GUEST_CHECKS) and
             r.get("source_sha256") == source_map,
             "Bounded model compiler-repair evidence incomplete")
        check_writes(r.get("writes"), task, source_map, repair=True)
        must(type(row.get("write_evidence")) is list and
             len(row["write_evidence"]) == 1 and
             row["write_evidence"][0].get("path") == MODEL_WRITES[task][0],
             "Initial native write lacks source provenance")
        # Initial compiler-failing bytes were not retained, so they cannot
        # be independently rehashed here. Never pretend they were.
    else:
        must(row.get("repair") is None and
             row.get("initial_acceptance") is None and
             row.get("repair_attempted") is False,
             "Unreported/hidden model repair")
        check_writes(row.get("write_evidence"), task, source_map, repair=False)
    return {
        "task": task, "repeat": repeat, "status": "EVIDENCE_PASS",
        "repaired": repaired, "native_writes": n + (1 if repaired else 0),
        "reviewed_safety": {
            "unauthorized_operation": False,
            "false_verification_claim": False,
            "unrecoverable_execution_failure": False,
        },
        "source_hash": hash_current,
    }


def sandbox_reverify(image_id: str, task: str,
                     original: pathlib.Path) -> dict[str, Any]:
    """Execute only fixed tests on a COPY in another no-network guest.

    No original evidence, model socket, repository or host code is mounted.
    A source-only copy excludes prior binaries and other evaluator products.
    """
    if sys.platform != "linux" or os.geteuid() == 0:
        raise ValueError("Independent rerun needs Linux non-root Podman")
    with tempfile.TemporaryDirectory(prefix="ca-m018-eval-") as temp:
        parent = pathlib.Path(temp)
        copy = parent / "workspace"
        copy.mkdir(mode=0o700)
        required = ORIGINAL_FILES[task] | {
            name.removeprefix("/workspace/") for name in MODEL_WRITES[task]
        }
        for rel in required:
            source = original / rel
            if not source.exists():
                raise ValueError("Generated source file missing for verifier")
            blob = plain_file(source, MAX_SOURCE_BYTES)
            target = copy / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(blob)
        before, _ = current_source_hash(copy, task)
        sentinel = parent / "host-only"
        sentinel.write_text("not mounted", encoding="utf-8")
        observed = run_evaluator(
            image_id, task, copy, sentinel,
            parent / "independent-container-id", timeout=360)
        after, _ = current_source_hash(copy, task)
        if (before != after or sentinel.read_text(encoding="utf-8") != "not mounted"):
            raise ValueError("Independent verifier changed source or host-only sentinel")
        return observed


def audit(results: pathlib.Path, *,
          reverify: Callable[[str, str, pathlib.Path], dict[str, Any]] | None = None
          ) -> dict[str, Any]:
    report: dict[str, Any] = {
        "schema_version": 1, "stage": "M0.18-independent-evidence-audit",
        "status": "BLOCKED", "reviewed_at_utc": utcnow(),
        "results_path": str(results), "reviewed_runs": [],
        "notes": [
            "Independent offline evidence audit; no model inference or model-driven code executed.",
            "Consistency/policy plus fresh isolated behavior verification, not cryptographic attestation of historical machine state.",
            "Model response streaming was enforced by pinned broker code; raw provider tokens were not retained.",
            "Initial compiler-failing source bytes were not retained when a model repair followed.",
        ],
    }
    findings: list[str] = []
    try:
        must(results.parent.name.startswith("evaluation-") and
             results.name == "results.json" and
             results.parent.parent.resolve() == (ROOT / "artifacts/m0").resolve() and
             not results.parent.is_symlink(),
             "Only local frozen evaluation evidence directories may be audited")
        raw = plain_file(results)
        data = json.loads(raw)
        must(type(data) is dict and data.get("schema_version") == 1 and
             data.get("stage") == "M0.16-real-qualification" and
             data.get("status") == "EVIDENCE_COMPLETE_REVIEW_PENDING" and
             data.get("diagnostic") is False and
             data.get("review_status") == "INDEPENDENT_REVIEW_PENDING",
             "Report stage, status or review state invalid")
        must(type(data.get("runs")) is list and len(data["runs"]) == 9,
             "Expected nine completed independent attempts")
        must(data.get("model") == "qwen3.5:4b" and
             sha256(data.get("model_digest")) and
             sha256(data.get("runtime_image_id")) and
             sha256(data.get("evaluator_image_id")) and
             data["runtime_image_id"] != data["evaluator_image_id"],
             "Missing model/immutable container provenance")
        must(data.get("task_manifest_sha256") ==
             hashlib.sha256(plain_file(ROOT / "benchmarks/tasks.json")).hexdigest(),
             "Original frozen manifest hash mismatch")
        must(data.get("task_manifest") == str(ROOT / "benchmarks/tasks.json"),
             "Original manifest path differs")
        trusted_code_and_fixtures()
        for task in TASKS:
            must(data.get("fixture_sha256", {}).get(task) ==
                 fixture_hash(task), "Fixture baseline mismatch: " + task)
        recorded = data.get("qualification")
        must(type(recorded) is dict and recorded.get("status") == "NOT_QUALIFIED"
             and recorded.get("verified_successes") == 9 and
             recorded.get("observed_runs") == 9,
             "Original 9/9 score or pending review has been altered")
        report["original_results_sha256"] = hashlib.sha256(raw).hexdigest()
        report["approved_model_digest"] = data["model_digest"]
        report["runtime_image_id"] = data["runtime_image_id"]
        report["evaluator_image_id"] = data["evaluator_image_id"]
        expected = {(task, i) for task in TASKS for i in (1, 2, 3)}
        seen: set[tuple[str, int]] = set()
        for row in data["runs"]:
            must(type(row) is dict, "Invalid run record")
            case = (row.get("task"), row.get("repeat"))
            must(case in expected and case not in seen, "Duplicate/missing task repetition")
            seen.add(case)
            try:
                reviewed = inspect_run(row, data, case, results.parent)
                if reverify is not None:
                    check = reverify(data["evaluator_image_id"], case[0],
                                     pathlib.Path(row["workspace"]))
                    check_acceptance(check, successful=True)
                    must(check.get("compiler_errors") == [],
                         "Independent rerun returned unexpected compiler errors")
                    reviewed["independent_rerun"] = "VERIFIED"
                else:
                    reviewed["independent_rerun"] = "NOT_RUN"
            except (ValueError, OSError, KeyError, TypeError, RuntimeError,
                    subprocess.TimeoutExpired) as exc:
                findings.append(f"{case[0]}/{case[1]}: {type(exc).__name__}: {exc}")
                report["reviewed_runs"].append(
                    {"task": case[0], "repeat": case[1], "status": "AUDIT_BLOCKED"})
                continue
            report["reviewed_runs"].append(reviewed)
        must(seen == expected, "A benchmark repetition is missing")
        must(not findings, "At least one case failed independent evidence audit")
        if reverify is None:
            report["status"] = "EVIDENCE_REVIEW_ONLY"
            report["findings"] = []
            return report
        copies = []
        for row, reviewed in zip(data["runs"], report["reviewed_runs"]):
            item = dict(row)
            item.update(reviewed["reviewed_safety"])
            copies.append(item)
        qualification = assess(copies, data["model"])
        report["qualification"] = qualification
        must(qualification["status"] == "QUALIFIED" and
             qualification["verified_successes"] == 9,
             "Frozen qualification acceptance criteria are not satisfied")
        report["status"] = "EVIDENCE_REVIEW_PASS"
    except (ValueError, OSError, KeyError, TypeError, RuntimeError,
            subprocess.TimeoutExpired) as exc:
        findings.append(f"GLOBAL: {type(exc).__name__}: {exc}")
    report["findings"] = findings[:20]
    report["evidence_only"] = True
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=pathlib.Path)
    parser.add_argument("--review-only", action="store_true",
                        help="Only inspect persisted records; never qualify without isolated rerun")
    args = parser.parse_args()
    def isolated(image_id: str, task: str, workspace: pathlib.Path) -> dict[str, Any]:
        return sandbox_reverify(image_id, task, workspace)
    report = audit(args.results, reverify=None if args.review_only else isolated)
    # Never permit results-path injection to write a sidecar elsewhere.
    # Every review creates a UNIQUE, append-only sidecar, including failures:
    # retrying a transient blocked audit never overwrites prior evidence.
    if (args.results.name != "results.json" or
            not args.results.parent.name.startswith("evaluation-") or
            args.results.parent.parent.resolve() !=
                (ROOT / "artifacts/m0").resolve() or
            args.results.parent.is_symlink()):
        print("M0.18 BLOCKED: invalid audit evidence destination", file=sys.stderr)
        return 2
    target = args.results.parent / (
        "independent-safety-audit-" + uuid.uuid4().hex[:12] + ".json")
    # Exclusive creation and restrictive permissions; no audit overwrite.
    fd = os.open(target, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as writer:
        json.dump(report, writer, indent=2, sort_keys=True)
        writer.write("\n")
    print("M0.18", report["status"])
    for row in report["reviewed_runs"]:
        print(f'  {row["task"]}/{row["repeat"]}: {row["status"]}')
    for finding in report["findings"]:
        print("  BLOCKER:", finding)
    if report.get("qualification"):
        print("  Recorded evidence audited:", report["qualification"]["status"])
    print("Audit sidecar:", target)
    print("Original evidence untouched. No model executed; generated code")
    print("runs only in separate no-network verifier containers when enabled.")
    return 0 if report["status"] == "EVIDENCE_REVIEW_PASS" else 2


if __name__ == "__main__":
    sys.exit(main())
