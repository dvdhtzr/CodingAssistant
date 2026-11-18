"""M0.11 fixed evaluator and rootless two-stage command contracts (model-free)."""
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))
from controlled_repair_pilot import (
    PROPOSAL_SCRIPT, EVALUATOR_SCRIPT, PROPOSAL_CHECKS, EVAL_CHECKS,
    proposal_command, evaluator_command, exact_checks,
)


class ControlledPilotTests(unittest.TestCase):
    def test_proposal_stage_only_private_socket_and_no_projects(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            private = root / "private"
            private.mkdir()
            sentinel = root / "outside"
            sentinel.touch()
            cmd = proposal_command("a"*64, 1000, 1000,
                                   private, "qwen3.5:4b", sentinel)
            self.assertEqual(cmd.count("--volume"), 1)
            self.assertTrue(cmd[cmd.index("--volume") + 1].endswith(":/run/codingassistant:ro"))
            self.assertEqual(cmd[cmd.index("--entrypoint=node") + 1], "a"*64)
            self.assertIn("--network=none", cmd)
            self.assertIn("--read-only", cmd)
            self.assertIn("--cap-drop=all", cmd)
            self.assertIn("--security-opt=no-new-privileges", cmd)
            self.assertIn("--pull=never", cmd)
            self.assertIn("--userns=keep-id", cmd)
            self.assertIn("No markdown", cmd[-1])
            self.assertIn("values.Summ()", cmd[-1])
            self.assertNotIn("--network=host", cmd)
            self.assertNotIn("--privileged", cmd)
            self.assertEqual(cmd[cmd.index("--eval") + 1],
                             PROPOSAL_SCRIPT.read_text(encoding="utf-8"))

    def test_independent_fixed_evaluator_mounts_only_disposable_copy(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            workspace = root / "workspace"
            workspace.mkdir()
            sentinel = root / "outside"
            sentinel.touch()
            cmd = evaluator_command("b"*64, 1000, 1000, workspace, sentinel)
            self.assertEqual(cmd.count("--volume"), 1)
            self.assertTrue(cmd[cmd.index("--volume") + 1].endswith(":/workspace:rw"))
            self.assertEqual(cmd[cmd.index("--entrypoint=node") + 1], "b"*64)
            for needed in (
                "--network=none", "--read-only", "--cap-drop=all",
                "--security-opt=no-new-privileges", "--pids-limit=64",
                "--memory=2g", "--pull=never", "--userns=keep-id",
                "--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup",
            ):
                self.assertIn(needed, cmd)
            self.assertNotIn("--privileged", cmd)
            self.assertNotIn("--network=host", cmd)
            self.assertEqual(cmd[cmd.index("--eval") + 1],
                             EVALUATOR_SCRIPT.read_text(encoding="utf-8"))

    def test_all_positive_checks_required(self):
        for names in (EVAL_CHECKS, PROPOSAL_CHECKS):
            report = {"checks": {key: True for key in names}}
            self.assertTrue(exact_checks(report, names))
            for name in names:
                altered = dict(report["checks"])
                altered[name] = False
                self.assertFalse(exact_checks({"checks": altered}, names))
            self.assertFalse(exact_checks({"checks": {}}, names))
            self.assertFalse(exact_checks(
                {"checks": {**report["checks"], "unexpected": True}}, names))

    def test_fixed_build_never_calls_model_or_shell(self):
        text = EVALUATOR_SCRIPT.read_text(encoding="utf-8")
        self.assertIn('spawnSync("dotnet"', text) if False else self.assertIn(
            'runFixed("dotnet"', text)
        self.assertIn('"restore", "Repair.csproj"', text)
        self.assertIn('"--source", "/tmp/m0-empty-nuget-source"', text)
        self.assertIn('"build", "Repair.csproj", "--no-restore"', text)
        self.assertIn('"Total: 15\\n"', text)
        self.assertNotIn("opencode", text.lower())
        self.assertNotIn("ollama", text.lower())
        self.assertNotIn("npm install", text)
        self.assertNotIn("git push", text)
        self.assertNotIn('shell: true', text)

    @unittest.skipUnless(shutil.which("node"), "Node is not installed in test runner")
    def test_both_node_sources_parse(self):
        for script in (PROPOSAL_SCRIPT, EVALUATOR_SCRIPT):
            with self.subTest(script=script):
                test = subprocess.run(
                    ["node", "--check", str(script)], capture_output=True, text=True, timeout=20)
                self.assertEqual(test.returncode, 0, test.stderr)
