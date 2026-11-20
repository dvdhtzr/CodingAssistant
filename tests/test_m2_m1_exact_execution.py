"""M2→M1 exact approval bridge tests: no model, Podman or remote calls."""
import contextlib
import importlib.util
import io
import pathlib
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT / "tools/m1"), str(ROOT / "tools/m0")]
spec = importlib.util.spec_from_file_location(
    "approved_gateway_coding", ROOT / "tools/m1/approved_gateway_coding.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
from run_ledger import RunLedger

CONTROL = {"task_id": "a" * 32, "run_id": "b" * 32,
           "plan_version": 1, "plan_hash": "c" * 64}

def plan(control=None):
    return m.build_approved_plan("1" * 64, "2" * 64, "3" * 64,
                                 "4" * 64, "5" * 64, control=control)

class ExactExecutableBoundaryTests(unittest.TestCase):
    def test_m2_binding_is_required_and_changes_exact_hash(self):
        no_control, old_hash = plan()
        bound, hash1 = plan(CONTROL)
        self.assertNotEqual(old_hash, hash1)
        self.assertNotIn("control", no_control)
        self.assertEqual(bound["control"], CONTROL)
        self.assertEqual(hash1, plan(dict(CONTROL))[1])
        for name, value in (
            ("task_id", "../host"),
            ("run_id", "X" * 32),
            ("plan_version", 0),
            ("plan_version", True),
            ("plan_hash", "z" * 64),
        ):
            tampered = dict(CONTROL, **{name: value})
            with self.subTest(name=name), self.assertRaises(ValueError):
                plan(tampered)
        for removed in CONTROL:
            changed = dict(CONTROL)
            changed.pop(removed)
            with self.assertRaises(ValueError):
                plan(changed)
        with self.assertRaises(ValueError):
            plan(dict(CONTROL, unapproved="surprise"))
        # No model may silently receive different scope after owner approval.
        for key in CONTROL:
            changed = dict(CONTROL)
            changed[key] = (changed[key] + "f" if key != "plan_version"
                            else 2)
            if key == "plan_version":
                self.assertNotEqual(hash1, plan(changed)[1])
            elif key == "plan_hash":
                with self.assertRaises(ValueError):
                    plan(changed)
            else:
                with self.assertRaises(ValueError):
                    plan(changed)

    def test_preview_cli_cannot_create_ledger_or_launch_agent(self):
        prepared, expected = plan(CONTROL)
        with mock.patch.object(m, "preflight", return_value=(
                prepared, expected, "2" * 64, "3" * 64,
                "5" * 64, "1" * 64)) as preflight, \
             mock.patch.object(m, "RunLedger",
                               side_effect=AssertionError("preview opened ledger")), \
             mock.patch.object(m, "execute",
                               side_effect=AssertionError("preview executed agent")), \
             mock.patch.object(sys, "argv", [
                "m1.py", "--preview-json", "--control-task-id", CONTROL["task_id"],
                "--control-run-id", CONTROL["run_id"],
                "--control-plan-version", "1",
                "--control-plan-hash", CONTROL["plan_hash"],
             ]):
            capture = io.StringIO()
            with contextlib.redirect_stdout(capture):
                self.assertEqual(m.main(), 0)
            import json
            output = json.loads(capture.getvalue())
            self.assertEqual(output["schema"],
                             "codingassistant-execution-preview-v1")
            self.assertEqual(output["plan_hash"], expected)
            self.assertEqual(output["plan"], prepared)
            self.assertFalse(preflight.call_args.kwargs["prepare"])

    def test_mismatch_from_stdin_never_launches_agent(self):
        prepared, expected = plan(CONTROL)
        with mock.patch.object(m, "preflight", return_value=(
                prepared, expected, "2" * 64, "3" * 64,
                "5" * 64, "1" * 64)), \
             mock.patch.object(m, "request_exact_approval",
                               side_effect=AssertionError("must not prompt")), \
             mock.patch.object(m, "run_gateway",
                               side_effect=AssertionError("coding without approval")):
            capture = io.StringIO()
            with contextlib.redirect_stdout(capture):
                result = m.execute(False, control=CONTROL,
                                   approval_from_stdin="APPROVE " + "d" * 64)
            self.assertEqual(result["status"], "NOT_APPROVED")
            self.assertFalse(result["approved"])
            self.assertEqual(result["plan_hash"], expected)

    def test_ledger_lookup_never_creates_or_replays_missing_request(self):
        with tempfile.TemporaryDirectory() as tmp:
            with RunLedger(pathlib.Path(tmp) / "private") as ledger:
                self.assertIsNone(ledger.get_by_request("0" * 32))
                run = ledger.start(request_id=CONTROL["run_id"])
                try:
                    self.assertEqual(ledger.get_by_request(
                        CONTROL["run_id"])["run_id"], run.run_id)
                    self.assertEqual(ledger.get_by_request(
                        CONTROL["run_id"])["state"], "NEW")
                finally:
                    run.close()
                with self.assertRaises(ValueError):
                    ledger.get_by_request("../../unsafe")

if __name__ == "__main__":
    unittest.main()
