"""M0.9 disposable-workspace model-free tests; never start Podman or an agent."""
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))
from disposable_workspace_probe import (
    CLIENT, EXPECTED, REQUIRED, checks_pass, command_for, fixture_hash
)


class WorkspaceTests(unittest.TestCase):
    def create_fixture(self, directory):
        root = pathlib.Path(directory)
        for name in EXPECTED:
            (root / name).write_text("test fixture " + name, encoding="utf-8")
        return root

    def test_hash_matches_copy_but_detects_modification(self):
        with tempfile.TemporaryDirectory() as temp:
            source = pathlib.Path(temp) / "source"
            copy = pathlib.Path(temp) / "copy"
            source.mkdir()
            self.create_fixture(source)
            original = fixture_hash(source)
            shutil.copytree(source, copy)
            self.assertEqual(fixture_hash(copy), original)
            (copy / "Program.cs").write_text("modified", encoding="utf-8")
            self.assertNotEqual(fixture_hash(copy), original)
            self.assertEqual(fixture_hash(source), original)

    def test_frozen_input_fails_closed_on_new_paths_and_links(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = pathlib.Path(temp) / "fixture"
            folder.mkdir()
            self.create_fixture(folder)
            extra = folder / "not-allowed"
            extra.write_text("extra")
            with self.assertRaises(ValueError):
                fixture_hash(folder)
            extra.unlink()
            program = folder / "Program.cs"
            program.unlink()
            try:
                program.symlink_to(folder / "Repair.csproj")
            except (OSError, NotImplementedError):
                self.skipTest("Symlinks unavailable")
            with self.assertRaises(ValueError):
                fixture_hash(folder)

    def test_only_disposable_workspace_mounted_with_immutable_id(self):
        with tempfile.TemporaryDirectory() as temp:
            p = pathlib.Path(temp)
            folder = p / "workspace"
            folder.mkdir()
            outside = p / "secret"
            outside.write_text("NOT MOUNTED", encoding="utf-8")
            args = command_for("a"*64, 1000, 1000, folder, outside)
            self.assertEqual(args.count("--volume"), 1)
            self.assertTrue(args[args.index("--volume") + 1].endswith(":/workspace:rw"))
            self.assertEqual(args[args.index("--entrypoint=node") + 1], "a"*64)
            for required in ("--pull=never", "--network=none", "--read-only",
                             "--cap-drop=all", "--security-opt=no-new-privileges",
                             "--pids-limit=64", "--memory=512m", "--userns=keep-id",
                             "--tmpfs=/tmp:rw,nosuid,nodev,size=64m,notmpcopyup"):
                self.assertIn(required, args)
            self.assertNotIn("--privileged", args)
            self.assertNotIn("--network=host", args)
            self.assertNotIn("--build", args)
            for image in ("latest", "a"*12, "", "../escape"):
                with self.subTest(image=image), self.assertRaises(ValueError):
                    command_for(image, 1000, 1000, folder, outside)

    def test_all_positive_and_negative_checks_are_mandatory(self):
        good = {"checks": {key: True for key in REQUIRED}}
        self.assertTrue(checks_pass(good))
        for key in REQUIRED:
            altered = dict(good["checks"])
            altered[key] = False
            self.assertFalse(checks_pass({"checks": altered}), key)
        self.assertFalse(checks_pass({"checks": {}}))
        self.assertFalse(checks_pass({"checks": {**good["checks"], "extra": True}}))

    def test_no_agent_model_or_production_workspace_run(self):
        code = CLIENT.read_text(encoding="utf-8")
        self.assertIn('spawnSync(process.execPath, ["-e", childSource, sentinel]', code)
        self.assertIn('"/workspace"', code)
        self.assertIn('"/proc/self/status"', code)
        self.assertNotIn("opencode run", code)
        self.assertNotIn("ollama", code.lower())
        self.assertNotIn("npm install", code)

    @unittest.skipUnless(shutil.which("node"), "Node unavailable on test host")
    def test_node_source_has_valid_syntax(self):
        result = subprocess.run(["node", "--check", str(CLIENT)], capture_output=True,
                                encoding="utf-8", text=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main()
