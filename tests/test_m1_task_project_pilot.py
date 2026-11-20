"""Task-specific pilot tests use no Podman, model, or network."""
import hashlib
import importlib.util
import json
import os
import pathlib
import stat
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT / "tools/m1"), str(ROOT / "tools/m0")]
spec = importlib.util.spec_from_file_location(
    "task_project_coding", ROOT / "tools/m1/task_project_coding.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


def contract():
    description = (
        "Create a local .NET 10 expense tracking API with expense add, list, "
        "and filter endpoints. Use built-in framework components only and "
        "do not call external services. Include basic validation, private "
        "local storage and independent tests of success and invalid inputs."
    )
    identity = "a" * 32
    version = 1
    approved_sha = m.sha(
        f"codingassistant-spec-v1\n{identity}\n{version}\n{description}".encode())
    return {
        "schema": m.SCHEMA,
        "taskId": identity,
        "specificationVersion": version,
        "approvedSpecificationSha256": approved_sha,
        "specificationSha256": m.sha(description.encode()),
        "objective": "Implement a local expense tracking API",
        "specification": description,
        "model": m.MODEL,
        "files": [
            "Expenses/Expenses.csproj",
            "Expenses/Program.cs",
            "Expenses.Tests/Expenses.Tests.csproj",
            "Expenses.Tests/Program.cs",
        ],
    }


class TaskPilotTests(unittest.TestCase):
    def write(self, root, content):
        path = pathlib.Path(root) / "contract.json"
        path.write_bytes(content)
        path.chmod(0o600)
        return path

    def test_accepts_approved_task_without_fixed_fixture(self):
        with tempfile.TemporaryDirectory() as temp:
            item = contract()
            path = self.write(temp, json.dumps(item).encode())
            value, raw, digest = m.read_contract(path)
            self.assertEqual(value["taskId"], "a" * 32)
            self.assertIn("Expenses/Program.cs", value["files"])
            self.assertNotIn("api/Repair.csproj", value["files"])
            self.assertEqual(digest, m.sha(raw))
            plan, plan_hash = m.execution_plan(
                value, digest, "b" * 64, "c" * 64, "d" * 64)
            self.assertEqual(plan["scope"], "disposable-project-only")
            self.assertFalse(plan["git_remote"])
            self.assertEqual(plan["max_agent_turns"], 1)
            self.assertNotIn("assignment", plan)
            self.assertEqual(len(plan_hash), 64)
            _, changed = m.execution_plan(
                value, digest, "b" * 64, "e" * 64, "d" * 64)
            self.assertNotEqual(plan_hash, changed)

    def test_fails_closed_on_spec_model_and_file_tampering(self):
        cases = [
            {"specificationVersion": 2},
            {"specification": "another unapproved application specification that is very long"},
            {"approvedSpecificationSha256": "f" * 64},
            {"specificationSha256": "e" * 64},
            {"model": "remote-provider"},
            {"objective": "x"},
            {"files": ["../Program.cs"]},
            {"files": ["/tmp/Program.cs"]},
            {"files": ["src/.git/config.cs"]},
            {"files": ["src/Program.cs", "SRC/program.cs"]},
            {"files": ["src/Program.cs", "src/package.json"]},
            {"files": ["src/Program.cs"]},
        ]
        with tempfile.TemporaryDirectory() as temp:
            for change in cases:
                value = {**contract(), **change}
                path = self.write(temp, json.dumps(value).encode())
                with self.subTest(change=change), self.assertRaises(
                        (ValueError, TypeError)):
                    m.read_contract(path)
            value = contract()
            encoded = (json.dumps(value)[:-1] + ',"model":"other"}').encode()
            with self.assertRaises(ValueError):
                m.read_contract(self.write(temp, encoded))
            path = self.write(temp, json.dumps(value).encode())
            linked = pathlib.Path(temp) / "linked.json"
            linked.symlink_to(path)
            with self.assertRaises(ValueError):
                m.read_contract(linked)


    @unittest.skipUnless(sys.platform == "linux", "Linux-only execution state gate")
    def test_live_sqlite_approval_invalidates_superseded_contract(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            db_path = root / "codingassistant.db"
            item = contract()
            with m.sqlite3.connect(db_path) as db:
                db.executescript("""
                    CREATE TABLE tasks(id TEXT PRIMARY KEY,objective TEXT);
                    CREATE TABLE interview_specs(
                      task_id TEXT,version INTEGER,sha256 TEXT,model TEXT,
                      content TEXT,status TEXT);
                    CREATE TABLE project_drafts(
                      id TEXT,task_id TEXT,spec_version INTEGER,
                      spec_sha256 TEXT,model TEXT,state TEXT,
                      created_utc TEXT);
                    CREATE TABLE project_draft_files(draft_id TEXT,path TEXT);
                """)
                db.execute("INSERT INTO tasks VALUES(?,?)", (
                    item["taskId"], item["objective"]))
                db.execute("INSERT INTO interview_specs VALUES(?,?,?,?,?,?)",
                           (item["taskId"], item["specificationVersion"],
                            item["approvedSpecificationSha256"], item["model"],
                            item["specification"], "Approved"))
                db.execute("INSERT INTO project_drafts VALUES(?,?,?,?,?,?,?)",
                           ("d" * 32, item["taskId"], item["specificationVersion"],
                            item["approvedSpecificationSha256"], item["model"],
                            "Generated", "2026-11-20T00:00:00Z"))
                db.executemany("INSERT INTO project_draft_files VALUES(?,?)",
                               [("d" * 32, x) for x in item["files"]])
            db_path.chmod(0o600)
            m.verify_live_approval(db_path, item)
            with m.sqlite3.connect(db_path) as db:
                db.execute("UPDATE interview_specs SET status='Superseded'")
            with self.assertRaises(ValueError):
                m.verify_live_approval(db_path, item)
            with m.sqlite3.connect(db_path) as db:
                db.execute("UPDATE interview_specs SET status='Approved'")
                db.execute("DELETE FROM project_draft_files WHERE path=?",
                           (item["files"][0],))
            with self.assertRaises(ValueError):
                m.verify_live_approval(db_path, item)

    def test_workspace_accepts_exact_source_files_only(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            expected = {"Service/Program.cs", "Service/Service.csproj"}
            (root / "Service").mkdir()
            (root / "Service" / "Program.cs").write_text("using System;\n")
            (root / "Service" / "Service.csproj").write_text("<Project/>\n")
            observed = m.source_snapshot(root, expected)
            self.assertEqual(set(observed), expected)
            (root / "Service" / "rogue.cs").write_text("malicious")
            with self.assertRaises(ValueError):
                m.source_snapshot(root, expected)
            (root / "Service" / "rogue.cs").unlink()
            (root / "Service" / "link.cs").symlink_to("Program.cs")
            with self.assertRaises(ValueError):
                m.source_snapshot(root, expected)

    def test_evaluator_is_nonroot_offline_and_has_no_model_socket(self):
        with tempfile.TemporaryDirectory() as temp:
            workspace = pathlib.Path(temp)
            with (mock.patch.object(m.os, "getuid", return_value=1000, create=True),
                  mock.patch.object(m.os, "getgid", return_value=1000, create=True)):
                args = m.evaluator_command(
                    "f" * 64, workspace, "Service/Service.csproj",
                    ["build", "/workspace/Service/Service.csproj", "--no-restore"])
            for argument in ("--rm", "--pull=never", "--network=none",
                             "--read-only", "--cap-drop=all",
                             "--security-opt=no-new-privileges",
                             "--userns=keep-id", "--pids-limit=96"):
                self.assertIn(argument, args)
            self.assertNotIn("--privileged", args)
            self.assertNotIn("--publish", args)
            self.assertNotIn("ollama.sock", " ".join(args))
            self.assertEqual(args[args.index("--entrypoint=dotnet") + 1], "f" * 64)

    def test_verification_cannot_claim_test_pass_for_zero_tests(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            path = {"Service/Service.csproj"}
            with mock.patch.object(m, "run_check", return_value={
                    "passed": True, "exit_code": 0,
                    "diagnostic_codes": [], "tests_observed": 0}) as fake:
                result = m.independent_evaluation("f" * 64, root, path)
                self.assertTrue(result["build_passed"])
                self.assertFalse(result["tests_passed"])
                self.assertEqual(fake.call_count, 2)
            path.add("Service.Tests/Service.Tests.csproj")
            with mock.patch.object(m, "run_check", side_effect=[
                    {"passed": True, "exit_code": 0, "tests_observed": 0},
                    {"passed": True, "exit_code": 0, "tests_observed": 0},
                    {"passed": True, "exit_code": 0, "tests_observed": 4},
                    {"passed": True, "exit_code": 0, "tests_observed": 0},
                    {"passed": True, "exit_code": 0, "tests_observed": 0},
            ]):
                result = m.independent_evaluation("f" * 64, root, path)
                self.assertTrue(result["tests_passed"])
                self.assertEqual(result["test_count"], 4)


if __name__ == "__main__":
    unittest.main()
