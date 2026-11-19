"""M0.15 evaluator contract tests: no Podman/network/model needed."""
from __future__ import annotations

import pathlib
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools/m0"))
import qualification_acceptance_probe as q
from qualify import TASKS


class IndependentAcceptanceContracts(unittest.TestCase):
    def test_exact_guard_and_acceptance_schema(self):
        self.assertEqual(len(q.GUARDS), 11)
        self.assertEqual(len(q.CHECKS), 8)
        for task in TASKS:
            success = {"task": task,
                       "guards": {name: True for name in q.GUARDS},
                       "checks": {name: True for name in q.CHECKS},
                       "verified": True, "compiler_errors": []}
            self.assertTrue(q.validate_result(success, task))
            false = dict(success)
            false["checks"] = dict(success["checks"])
            false["checks"]["feature_behavior"] = False
            false["verified"] = False
            self.assertTrue(q.validate_result(false, task))
            false["verified"] = True
            self.assertFalse(q.validate_result(false, task))
            failure = dict(success)
            failure["guards"] = dict(success["guards"])
            failure["guards"]["no_new_privileges"] = False
            failure["verified"] = False
            self.assertTrue(q.validate_result(failure, task))
            failure["verified"] = True
            self.assertFalse(q.validate_result(failure, task))

    def test_compiler_error_boundary(self):
        task = "01-api-feature"
        good = {"task": task,
                "guards": {name: True for name in q.GUARDS},
                "checks": {name: True for name in q.CHECKS},
                "verified": False, "compiler_errors": [
                    "CS0246: The type or namespace name 'FromQuery' could not be found"
                ]}
        good["checks"]["offline_build"] = False
        self.assertTrue(q.validate_result(good, task))
        for bad_value in (
            ["CS0246: test\nsecret"],
            ["not-an-error"],
            ["CS0246: " + "x" * 250],
            ["CS0001: OK"] * 6,
            "CS0246: not an array",
        ):
            self.assertFalse(q.validate_result(
                dict(good, compiler_errors=bad_value), task))
        with_build = dict(good)
        with_build["checks"] = dict(good["checks"], offline_build=True)
        self.assertFalse(q.validate_result(with_build, task))

    def test_unsafe_or_malformed_json_contract_fails_closed(self):
        self.assertFalse(q.validate_result(None, TASKS[0]))
        self.assertFalse(q.validate_result({}, TASKS[0]))
        self.assertFalse(q.validate_result({
            "task": TASKS[0], "guards": {}, "checks": {},
            "verified": True, "compiler_errors": []}, TASKS[0]))
        good = {"task": TASKS[0],
                "guards": {name: True for name in q.GUARDS},
                "checks": {name: True for name in q.CHECKS},
                "verified": True, "compiler_errors": []}
        for variant in ("true", 1, None, []):
            bad = dict(good, verified=variant)
            self.assertFalse(q.validate_result(bad, TASKS[0]))
        with_extra = dict(good)
        with_extra["guards"] = dict(good["guards"], extra=True)
        self.assertFalse(q.validate_result(with_extra, TASKS[0]))

    def test_podman_only_exact_disposable_workspace_mount(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = pathlib.Path(temp)
            workspace = folder / "workspace"
            workspace.mkdir()
            sentinel = folder / "host-only"
            sentinel.touch()
            cmd = q.evaluator_command("a" * 64, 1001, 1001,
                                      workspace, sentinel, TASKS[2])
            self.assertEqual(cmd.count("--volume"), 1)
            self.assertIn(f"{workspace.resolve()}:/workspace:rw", cmd)
            self.assertEqual(cmd[-3:], ["--", TASKS[2], str(sentinel.resolve())])
            for arg in ("--network=none", "--read-only", "--cap-drop=all",
                        "--security-opt=no-new-privileges", "--userns=keep-id",
                        "--pull=never", "--memory=3g", "--pids-limit=96"):
                self.assertIn(arg, cmd)
            self.assertNotIn("--privileged", cmd)
            self.assertNotIn("--network=host", cmd)
            self.assertNotIn("--volume=/home", " ".join(cmd))

    def test_wrong_task_or_symlink_denied(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = pathlib.Path(temp)
            workspace = folder / "workspace"
            workspace.mkdir()
            sentinel = folder / "host-only"
            sentinel.touch()
            with self.assertRaises(ValueError):
                q.evaluator_command("a" * 64, 1001, 1001, workspace,
                                    sentinel, "evil")
            linked = folder / "linked"
            try:
                linked.symlink_to(workspace, target_is_directory=True)
            except OSError:
                self.skipTest("Host does not support symlinks")
            with self.assertRaises(ValueError):
                q.evaluator_command("a" * 64, 1001, 1001, linked,
                                    sentinel, TASKS[0])

    def test_fixed_evaluator_never_forwards_model_code_or_network(self):
        source = q.CLIENT.read_text(encoding="utf-8")
        self.assertIn("NPM_CONFIG_OFFLINE", source)
        self.assertIn("NuGetAudit=false", source)
        self.assertIn("TaskSummary.acceptance.test.tsx", source)
        self.assertIn("frontend_behavior", source)
        self.assertIn("SIGKILL", source)
        self.assertNotIn("npm install", source)
        self.assertNotIn("ollama.sock\", \"", source)
        self.assertNotIn("shell: true", source)


if __name__ == "__main__":
    unittest.main()
