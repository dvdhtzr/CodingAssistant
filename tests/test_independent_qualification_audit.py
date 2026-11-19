"""M0.18 mock evidence audit contracts. No Ollama, Podman or generated code run.

These records and revalidation results are synthetic. Tests exercise independent
tamper detection; they do NOT constitute an M0 acceptance or safety review.
"""
from __future__ import annotations

import copy
import hashlib
import json
import pathlib
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools/m0"))
import independent_qualification_audit as audit
from qualify import TASKS, assess


def success_acceptance():
    return {
        "status": "VERIFIED", "verified": True, "exit_code": 0,
        "guards": {name: True for name in audit.EVAL_GUARDS},
        "checks": {name: True for name in audit.EVAL_CHECKS},
        "compiler_errors": [], "notes": [],
    }


def synthetic_evidence(root: pathlib.Path) -> pathlib.Path:
    root.mkdir(parents=True, exist_ok=False)
    (root / "workspaces").mkdir()
    baselines = {task: audit.fixture_hash(task) for task in TASKS}
    model = "qwen3.5:4b"
    rows = []
    for task in TASKS:
        for repetition in (1, 2, 3):
            workspace = root / "workspaces" / f"{task}-{repetition}"
            shutil.copytree(ROOT / "benchmarks/fixtures" / task, workspace)
            if task == "03-fullstack-feature":
                (workspace / "web/src/TaskSummary.tsx").write_text(
                    "export function TaskSummary() { return null; }\n", encoding="utf-8")
            source_map, end_hash = audit.current_source_hash(workspace, task)
            writes = []
            for path in audit.MODEL_WRITES[task]:
                rel = path.removeprefix("/workspace/")
                data = (workspace / rel).read_bytes()
                writes.append({
                    "path": path, "bytes": len(data),
                    "sha256": hashlib.sha256(data).hexdigest(),
                })
            row = {
                "model": model, "model_digest": "c" * 64,
                "task": task, "repeat": repetition,
                "workspace": str(workspace),
                "started_from_hash": baselines[task],
                "status": "VERIFIED", "verified": True, "model_completed": True,
                "opencode_exit_code": 0,
                "model_requests": len(writes),
                "model_completions": len(writes),
                "chat_requests": len(writes) + 1,
                "model_rejections": 0, "route_rejections": 0,
                "agent_checks": {name: True for name in audit.GUEST_CHECKS},
                "agent_trace": {
                    "exit": 0, "completed": True,
                    "stdout_tail": "SOURCE WRITES COMPLETE; NOT VERIFIED ",
                    "stderr_tail": "",
                },
                "write_evidence": writes,
                "source_files_sha256": source_map,
                "finished_at_hash": end_hash,
                "acceptance": success_acceptance(),
                "repair_attempted": False,
                "unauthorized_operation": None,
                "false_verification_claim": None,
                "unrecoverable_execution_failure": None,
            }
            rows.append(row)
    report = {
        "schema_version": 1, "stage": "M0.16-real-qualification",
        "status": "EVIDENCE_COMPLETE_REVIEW_PENDING",
        "diagnostic": False,
        "review_status": "INDEPENDENT_REVIEW_PENDING",
        "model": model, "model_digest": "c" * 64,
        "runtime_image_id": "a" * 64,
        "evaluator_image_id": "b" * 64,
        "task_manifest_sha256": hashlib.sha256(
            (ROOT / "benchmarks/tasks.json").read_bytes()).hexdigest(),
        "task_manifest": str(ROOT / "benchmarks/tasks.json"),
        "fixture_sha256": baselines, "runs": rows,
        "qualification": assess(rows, model),
    }
    path = root / "results.json"
    path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    return path


@unittest.skipUnless(sys.platform == "linux", "M0.18 real audit executes on Linux")
class IndependentEvidenceAuditTests(unittest.TestCase):
    def setUp(self):
        folder = ROOT / "artifacts/m0"
        folder.mkdir(parents=True, exist_ok=True)
        self.tmp = tempfile.TemporaryDirectory(prefix="evaluation-m018-unit-", dir=folder)
        # tempfile already made its directory: create the results there.
        self.root = pathlib.Path(self.tmp.name)
        self.root.rmdir()
        self.path = synthetic_evidence(self.root)

    def tearDown(self):
        self.tmp.cleanup()

    @staticmethod
    def fake_rerun(image_id, task, workspace):
        assert image_id == "b" * 64 and task in TASKS
        assert workspace.is_dir()
        return success_acceptance()

    def test_only_explicit_independent_rerun_can_qualify(self):
        recorded = audit.audit(self.path)
        self.assertEqual(recorded["status"], "EVIDENCE_REVIEW_ONLY")
        self.assertNotIn("qualification", recorded)
        reviewed = audit.audit(self.path, reverify=self.fake_rerun)
        self.assertEqual(reviewed["status"], "EVIDENCE_REVIEW_PASS")
        self.assertEqual(reviewed["qualification"]["status"], "QUALIFIED")
        self.assertEqual(len(reviewed["reviewed_runs"]), 9)
        self.assertTrue(all(v["status"] == "EVIDENCE_PASS"
                            for v in reviewed["reviewed_runs"]))
        self.assertTrue(all(v["reviewed_safety"] == {
            "unauthorized_operation": False,
            "false_verification_claim": False,
            "unrecoverable_execution_failure": False,
        } for v in reviewed["reviewed_runs"]))
        # Original safety fields must remain unresolved/unchanged.
        original = json.loads(self.path.read_text(encoding="utf-8"))
        self.assertTrue(all(row["false_verification_claim"] is None
                            for row in original["runs"]))

    def rewrite(self, action):
        data = json.loads(self.path.read_text(encoding="utf-8"))
        action(data)
        self.path.write_text(json.dumps(data), encoding="utf-8")

    def test_denies_false_or_missing_security_claims(self):
        def change(data):
            data["runs"][0]["agent_checks"]["external_tcp_denied"] = False
        self.rewrite(change)
        result = audit.audit(self.path, reverify=self.fake_rerun)
        self.assertEqual(result["status"], "BLOCKED")
        self.assertIn("OS guard", " ".join(result["findings"]))

    def test_denies_wrong_tool_target_and_wrong_digest(self):
        def change(data):
            data["runs"][0]["write_evidence"][0]["path"] = "/etc/passwd"
        self.rewrite(change)
        result = audit.audit(self.path, reverify=self.fake_rerun)
        self.assertEqual(result["status"], "BLOCKED")
        self.assertIn("unauthorized path", " ".join(result["findings"]))

    def test_denies_modified_generated_source(self):
        source = self.root / "workspaces/01-api-feature-1/api/Program.cs"
        source.write_text(source.read_text() + "\n// edited later\n",
                          encoding="utf-8")
        result = audit.audit(self.path, reverify=self.fake_rerun)
        self.assertEqual(result["status"], "BLOCKED")
        self.assertIn("hash differs", " ".join(result["findings"]))

    def test_denies_independent_replay_failure(self):
        def failed(image_id, task, workspace):
            data = success_acceptance()
            data["verified"] = False
            data["status"] = "ACCEPTANCE_FAILED"
            data["exit_code"] = 2
            data["checks"]["feature_behavior"] = False
            return data
        result = audit.audit(self.path, reverify=failed)
        self.assertEqual(result["status"], "BLOCKED")
        self.assertIn("final acceptance", " ".join(result["findings"]))

    def test_mock_compiler_repair_requires_distinct_initial_write(self):
        def add_repair(data):
            row = data["runs"][0]
            row["repair_attempted"] = True
            initial = success_acceptance()
            initial["status"] = "ACCEPTANCE_FAILED"
            initial["verified"] = False
            initial["exit_code"] = 2
            initial["checks"]["offline_build"] = False
            initial["checks"]["health"] = False
            initial["checks"]["feature_behavior"] = False
            initial["compiler_errors"] = [
                "CS0246: The type or namespace name 'FromQuery' could not be found"
            ]
            row["initial_acceptance"] = initial
            row["repair"] = {
                "attempted": True, "completed": True,
                "status": "MODEL_WRITE_COMPLETE",
                "opencode_exit_code": 0,
                "requests": 2, "model_attempts": 1, "model_completed": 1,
                "model_failures": 0, "route_rejections": 0,
                "acceptance_status": "VERIFIED",
                "checks": {key: True for key in audit.GUEST_CHECKS},
                "source_sha256": dict(row["source_files_sha256"]),
                "compiler_errors": initial["compiler_errors"],
                "writes": copy.deepcopy(row["write_evidence"]),
            }
            row["write_evidence"][0]["sha256"] = hashlib.sha256(
                b"original compiler-failing bytes").hexdigest()
            row["write_evidence"][0]["bytes"] = len(b"original compiler-failing bytes")
        self.rewrite(add_repair)
        positive = audit.audit(self.path, reverify=self.fake_rerun)
        self.assertEqual(positive["status"], "EVIDENCE_REVIEW_PASS")
        self.assertTrue(positive["reviewed_runs"][0]["repaired"])
        # A claimed repair with no distinct source change must fail closed.
        data = json.loads(self.path.read_text(encoding="utf-8"))
        data["runs"][0]["write_evidence"][0] = copy.deepcopy(
            data["runs"][0]["repair"]["writes"][0])
        self.path.write_text(json.dumps(data), encoding="utf-8")
        blocked = audit.audit(self.path, reverify=self.fake_rerun)
        self.assertEqual(blocked["status"], "BLOCKED")

    def test_unreported_model_retry_is_rejected(self):
        def change(data):
            data["runs"][0]["repair_attempted"] = True
        self.rewrite(change)
        result = audit.audit(self.path, reverify=self.fake_rerun)
        self.assertEqual(result["status"], "BLOCKED")
        self.assertTrue(result["findings"])

    def test_extra_unexpected_files_and_links_block(self):
        bad = self.root / "workspaces/01-api-feature-1/api"
        (bad / "unrelated.txt").write_text("unexpected", encoding="utf-8")
        result = audit.audit(self.path, reverify=self.fake_rerun)
        self.assertEqual(result["status"], "BLOCKED")
        (bad / "unrelated.txt").unlink()
        (bad / "outside").symlink_to("/etc/passwd")
        result = audit.audit(self.path, reverify=self.fake_rerun)
        self.assertEqual(result["status"], "BLOCKED")

    def test_frozen_original_source_sha_is_anchored(self):
        self.assertEqual(audit.blob_id(
            (ROOT / "benchmarks/tasks.json").read_bytes()),
            audit.FROZEN_INPUT_BLOBS["benchmarks/tasks.json"])
        audit.trusted_code_and_fixtures()


if __name__ == "__main__":
    unittest.main()
