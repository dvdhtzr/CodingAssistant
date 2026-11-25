"""Model-free M3 result contracts for the existing immutable M1 evidence ledger."""
import importlib.util
import hashlib
import json
import os
import pathlib
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT / "tools/m1"), str(ROOT / "tools/m0")]
spec = importlib.util.spec_from_file_location(
    "approved_gateway_for_results", ROOT / "tools/m1/approved_gateway_coding.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
REQUEST = "c" * 32
HASH = "d" * 64
EVIDENCE = "m112-approved-coding-" + "a" * 12 + ".json"


def report():
    return {
        "plan_hash": HASH,
        "status": "GATEWAY_APPROVED_CODING_PASS",
        "phase": "complete",
        "reason_code": None,
        "evaluator": {"verified": True, "status": "VERIFIED"},
        "source_before": {"api/Program.cs": "1" * 64},
        "source_after": {"api/Program.cs": "2" * 64},
        "broker": {"verified": 4},
        "approved_patch":
            "--- a/api/Program.cs\n+++ b/api/Program.cs\n@@ -1 +1 @@\n-old\n+new\n",
        "patch_omitted": False,
    }


class ResultReadoutContracts(unittest.TestCase):
    def test_fixed_verified_patch_summary(self):
        value = runner.bounded_result(report(), REQUEST, HASH)
        self.assertEqual(value["status"], "GATEWAY_APPROVED_CODING_PASS")
        self.assertTrue(value["evaluatorVerified"])
        self.assertTrue(value["sourceChanged"])
        self.assertEqual(value["inferenceRequests"], 4)
        self.assertIn("+new", value["patch"])
        self.assertNotIn("evidence_name", value)

    def test_old_run_without_saved_patch_has_still_evidence(self):
        old = report()
        old.pop("approved_patch")
        old.pop("patch_omitted")
        value = runner.bounded_result(old, REQUEST, HASH)
        self.assertEqual(value["patch"], "")
        self.assertTrue(value["sourceChanged"])
        self.assertTrue(value["evaluatorVerified"])

    def test_unverified_output_never_exposes_patch(self):
        bad = report()
        bad["status"] = "BLOCKED"
        bad["phase"] = "offline_acceptance"
        bad["reason_code"] = "ValueError"
        bad["evaluator"]["verified"] = False
        value = runner.bounded_result(bad, REQUEST, HASH)
        self.assertEqual(value["patch"], "")
        self.assertEqual(value["reasonCode"], "ValueError")

    def test_unbound_and_unapproved_patch_rejected(self):
        with self.assertRaises(ValueError):
            runner.bounded_result(report(), REQUEST, "f" * 64)
        bad = report()
        bad["approved_patch"] = "--- a/.env\n+SECRET\n"
        with self.assertRaises(ValueError):
            runner.bounded_result(bad, REQUEST, HASH)

    @unittest.skipUnless(os.name == "posix", "Linux owner-private evidence files")
    def test_private_saved_result_loaded_by_exact_request_id(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            evidence = root / EVIDENCE
            evidence.write_text(json.dumps(report()), encoding="utf-8")
            evidence.chmod(0o600)
            row = {"state": "SUCCEEDED", "plan_hash": HASH,
                   "evidence_name": EVIDENCE}
            ledger = mock.Mock()
            ledger.get_by_request.return_value = row
            with mock.patch.object(runner, "ARTIFACTS", root):
                value = runner.read_result_by_request(ledger, REQUEST)
            self.assertTrue(value["available"])
            self.assertEqual(value["runId"], REQUEST)
            ledger.get_by_request.assert_called_once_with(REQUEST)

    def test_active_or_missing_report_does_not_claim_verified(self):
        ledger = mock.Mock()
        ledger.get_by_request.return_value = None
        self.assertFalse(runner.read_result_by_request(ledger, REQUEST)["available"])
        ledger.get_by_request.return_value = {
            "state": "RUNNING", "plan_hash": HASH, "evidence_name": EVIDENCE,
        }
        self.assertFalse(runner.read_result_by_request(ledger, REQUEST)["available"])

    @unittest.skipUnless(os.name == "posix", "Linux O_NOFOLLOW evidence boundary")
    def test_symlink_report_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            safe = root / "legitimate.json"
            safe.write_text(json.dumps(report()), encoding="utf-8")
            safe.chmod(0o600)
            (root / EVIDENCE).symlink_to(safe)
            row = {"state": "SUCCEEDED", "plan_hash": HASH,
                   "evidence_name": EVIDENCE}
            ledger = mock.Mock()
            ledger.get_by_request.return_value = row
            with mock.patch.object(runner, "ARTIFACTS", root):
                with self.assertRaises(OSError):
                    runner.read_result_by_request(ledger, REQUEST)

    def test_bound_evidence_hash_detects_tampering_and_wrong_run(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            evidence = root / EVIDENCE
            value = {**report(), "run_id": "a"*24, "approved": True,
                     "gateway": {"removed": True},
                     "fixture_unchanged": True, "sentinel_unchanged": True}
            body = json.dumps(value).encode("utf-8")
            evidence.write_bytes(body)
            evidence.chmod(0o600)
            row = {"state": "SUCCEEDED", "plan_hash": HASH, "run_id": "a"*24,
                   "evidence_name": EVIDENCE, "evidence_sha256": hashlib.sha256(body).hexdigest()}
            ledger = mock.Mock()
            ledger.get_by_request.return_value = row
            with mock.patch.object(runner, "ARTIFACTS", root):
                self.assertTrue(runner.read_result_by_request(ledger, REQUEST)["available"])
                row["run_id"] = "b"*24
                with self.assertRaises(ValueError):
                    runner.read_result_by_request(ledger, REQUEST)
                row["run_id"] = "a"*24
                evidence.write_bytes(body + b" ")
                with self.assertRaises(ValueError):
                    runner.read_result_by_request(ledger, REQUEST)


if __name__ == "__main__":
    unittest.main()
