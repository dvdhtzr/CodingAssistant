"""M0.14 frozen 3x3 isolation readiness: unit tests, NO model or container."""
from __future__ import annotations

import pathlib
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))

import qualification_isolation_probe as q
from qualify import TASKS


class QualificationIsolationContracts(unittest.TestCase):
    def test_exact_frozen_input_matrix(self):
        self.assertEqual(set(TASKS), set(q.EXPECTED_FILES))
        self.assertEqual(len(TASKS) * 3, 9)
        self.assertEqual(len(q.REQUIRED), 18)
        self.assertEqual(len(q.check_manifest()), 64)
        for task in TASKS:
            with self.subTest(task=task):
                digest = q.sealed_snapshot(q.FIXTURES / task, task)
                self.assertEqual(len(digest), 64)
                with tempfile.TemporaryDirectory() as temp:
                    copy = pathlib.Path(temp) / "copy"
                    shutil.copytree(q.FIXTURES / task, copy)
                    self.assertEqual(q.sealed_snapshot(copy, task), digest)
                    self.assertNotIn(".git", set(p.name for p in copy.iterdir()))

    def test_added_file_denied(self):
        with tempfile.TemporaryDirectory() as temp:
            workspace = pathlib.Path(temp) / "copy"
            shutil.copytree(q.FIXTURES / TASKS[0], workspace)
            (workspace / ".env").write_text("private", encoding="utf-8")
            with self.assertRaises(ValueError):
                q.sealed_snapshot(workspace, TASKS[0])

    def test_missing_file_denied(self):
        with tempfile.TemporaryDirectory() as temp:
            workspace = pathlib.Path(temp) / "copy"
            shutil.copytree(q.FIXTURES / TASKS[0], workspace)
            (workspace / "api" / "Api.csproj").unlink()
            with self.assertRaises(ValueError):
                q.sealed_snapshot(workspace, TASKS[0])

    def test_hardlinked_file_denied(self):
        with tempfile.TemporaryDirectory() as temp:
            workspace = pathlib.Path(temp) / "copy"
            shutil.copytree(q.FIXTURES / TASKS[0], workspace)
            source = workspace / "api" / "Program.cs"
            extra = pathlib.Path(temp) / "hardlink"
            try:
                extra.hardlink_to(source)
            except OSError:
                self.skipTest("Host filesystem does not support hard links")
            with self.assertRaises(ValueError):
                q.sealed_snapshot(workspace, TASKS[0])

    def test_linked_directory_denied(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            linked = root / "linked"
            try:
                linked.symlink_to(q.FIXTURES / TASKS[0], target_is_directory=True)
            except OSError:
                self.skipTest("Host does not permit symlink creation")
            with self.assertRaises(ValueError):
                q.sealed_snapshot(linked, TASKS[0])

    def test_model_free_podman_command_has_only_disposable_mount(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            workspace = root / "workspace"
            workspace.mkdir()
            sentinel = root / "not-mounted"
            sentinel.touch()
            cmd = q.guest_command("a" * 64, 1001, 1001, workspace,
                                  sentinel, TASKS[2])
            self.assertEqual(cmd.count("--volume"), 1)
            self.assertIn(f"{workspace.resolve()}:/workspace:rw", cmd)
            for guard in ("--network=none", "--pull=never", "--read-only",
                          "--cap-drop=all", "--security-opt=no-new-privileges",
                          "--pids-limit=96", "--memory=2g", "--userns=keep-id",
                          "--workdir=/workspace"):
                self.assertIn(guard, cmd)
            self.assertNotIn("--privileged", cmd)
            self.assertNotIn("--network=host", cmd)
            self.assertNotIn("--volume=/home", " ".join(cmd))
            self.assertEqual(cmd[-3:], ["--", TASKS[2], str(sentinel.resolve())])
            self.assertEqual(cmd[cmd.index("--eval") + 1],
                             q.CLIENT.read_text(encoding="utf-8"))

    def test_unsupported_task_denied(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            root.mkdir(exist_ok=True)
            sentinel = root / "not-mounted"
            sentinel.touch()
            with self.assertRaises(ValueError):
                q.guest_command("a" * 64, 1001, 1001, root, sentinel, "bad-task")
            with self.assertRaises(ValueError):
                q.sealed_snapshot(q.FIXTURES / TASKS[0], "bad-task")

    def test_fail_closed_when_rootless_unavailable(self):
        with patch.object(q.sys, "platform", "linux"), patch.object(q.os, "geteuid",
                             return_value=1001), patch.object(q, "inspect_podman",
                             return_value={"rootless": False}):
            with tempfile.TemporaryDirectory() as temp:
                report = q.evaluate(90, pathlib.Path(temp) / "evidence.json")
            self.assertEqual(report["status"], "BLOCKED")
            self.assertEqual(report["runs"], [])
            self.assertFalse(report["model_used"])
            self.assertFalse(report["generated_code_executed"])

    def test_strict_guest_check_set(self):
        good = {"checks": {name: True for name in q.REQUIRED}}
        self.assertTrue(q.exact_checks(good, q.REQUIRED))
        for key in q.REQUIRED:
            bad = {"checks": dict(good["checks"])}
            bad["checks"][key] = False
            self.assertFalse(q.exact_checks(bad, q.REQUIRED))
        self.assertFalse(q.exact_checks({"checks": {}}, q.REQUIRED))

    def test_no_unsafe_legacy_benchmark_invocation(self):
        guest = q.CLIENT.read_text(encoding="utf-8")
        source = pathlib.Path(q.__file__).read_text(encoding="utf-8")
        self.assertNotIn("benchmark.py", source)
        self.assertNotIn("--acknowledge-unisolated-m0-risk", source)
        self.assertNotIn("ollama_json(", source)
        self.assertNotIn("npm install", guest)
        self.assertIn("child_external_tcp_denied", guest)


if __name__ == "__main__":
    unittest.main()
