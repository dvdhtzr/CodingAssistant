"""Model-free, dependency-free M0 harness tests."""
import json
import pathlib
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))
from common import endpoint_url, write_json
from qualify import TASKS, assess
from benchmark import source_hash, load_tasks

class M0Tests(unittest.TestCase):
    def test_manifest_contains_three_fixed_tasks(self):
        self.assertEqual(set(load_tasks()), set(TASKS))

    def test_qualification_rejects_missing_runs(self):
        self.assertEqual(assess([], "qwen3.5:4b")["status"], "NOT_QUALIFIED")

    def test_qualification_accepts_only_verified_and_reviewed_results(self):
        records = [{"model": "candidate", "task": task, "repeat": n, "verified": n != 3,
                    "unauthorized_operation": False, "false_verification_claim": False,
                    "unrecoverable_execution_failure": False}
                   for task in TASKS for n in range(1, 4)]
        self.assertEqual(assess(records, "candidate")["status"], "NOT_QUALIFIED")
        records[2]["verified"] = True
        self.assertEqual(assess(records, "candidate")["status"], "QUALIFIED")
        records[1]["unauthorized_operation"] = True
        self.assertEqual(assess(records, "candidate")["status"], "NOT_QUALIFIED")

    def test_qualification_requires_independent_safety_review(self):
        records = [{"model": "m", "task": task, "repeat": n, "verified": True}
                   for task in TASKS for n in range(1, 4)]
        self.assertEqual(assess(records, "m")["status"], "NOT_QUALIFIED")

    def test_endpoint_rejects_credentials_and_bad_schemes(self):
        self.assertEqual(endpoint_url("http://127.0.0.1:11434", "/api/tags"),
                         "http://127.0.0.1:11434/api/tags")
        self.assertEqual(endpoint_url("http://127.0.0.1:11434/v1", "/api/tags"),
                         "http://127.0.0.1:11434/api/tags")
        for invalid in ("file:///tmp/socket", "http://user:password@example.org",
                        "http://localhost/?token=secret"):
            with self.assertRaises(ValueError):
                endpoint_url(invalid, "/api/tags")

    def test_source_hash_changes_with_content(self):
        with tempfile.TemporaryDirectory() as folder:
            p = pathlib.Path(folder) / "app.cs"
            p.write_text("one", encoding="utf-8")
            before = source_hash(pathlib.Path(folder))
            p.write_text("two", encoding="utf-8")
            self.assertNotEqual(before, source_hash(pathlib.Path(folder)))

    def test_write_json_is_valid(self):
        with tempfile.TemporaryDirectory() as folder:
            p = pathlib.Path(folder) / "report.json"
            write_json(p, {"hello": "world"})
            self.assertEqual(json.loads(p.read_text()), {"hello": "world"})

if __name__ == "__main__":
    unittest.main()
