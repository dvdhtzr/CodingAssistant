"""M0.8b-2b transport isolation and evidence acceptance, no Ollama calls."""
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))
from isolated_opencode_real_probe import SCRIPT, REQUIRED, command_for, checks_pass


class IsolatedRealTests(unittest.TestCase):
    def test_image_pin_rootless_and_only_unix_mount(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = pathlib.Path(temp)
            private = folder / "private"
            private.mkdir(mode=0o700)
            sentinel = folder / "sentinel"
            sentinel.write_text("no guest access")
            args = command_for("a"*64, 1000, 1000, private, "qwen3.5:4b", sentinel)
            self.assertEqual(args.count("--volume"), 1)
            self.assertTrue(args[args.index("--volume") + 1].endswith(":/run/codingassistant:ro"))
            for flag in (
                "--pull=never", "--network=none", "--read-only", "--cap-drop=all",
                "--security-opt=no-new-privileges", "--userns=keep-id",
                "--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup",
                "--pids-limit=96", "--memory=2g", "--entrypoint=node",
            ):
                self.assertIn(flag, args)
            self.assertNotIn("--privileged", args)
            self.assertNotIn("--network=host", args)
            self.assertEqual(args[args.index("--entrypoint=node") + 1], "a"*64)
            for bad in ("mutable:latest", "a"*16, "../image", ""):
                with self.subTest(bad=bad), self.assertRaises(ValueError):
                    command_for(bad, 1000, 1000, private, "qwen3.5:4b", sentinel)

    def test_every_guard_required(self):
        good = {"checks": {key: True for key in REQUIRED}}
        self.assertTrue(checks_pass(good))
        for key in REQUIRED:
            copy = dict(good["checks"])
            copy[key] = False
            self.assertFalse(checks_pass({"checks": copy}), key)
        self.assertFalse(checks_pass({"checks": {}}))
        self.assertFalse(checks_pass({"checks": {**good["checks"], "extra": True}}))

    def test_only_one_fixed_opencode_prompt_and_no_other_agents(self):
        text = SCRIPT.read_text(encoding="utf-8")
        self.assertIn('spawn("opencode", [', text)
        self.assertIn('"run", "--standalone"', text)
        self.assertIn('"--title", "M0 bounded real inference"', text)
        self.assertIn('"--model", "ollama/" + model', text)
        self.assertIn("chatCount > 1", text)
        self.assertIn('server.listen(0, "127.0.0.1"', text)
        self.assertNotIn('child.kill("SIGTERM")', text)
        self.assertNotIn('execSync(', text)
        self.assertNotIn('opencode run --auto', text)

    @unittest.skipUnless(shutil.which("node"), "Node absent on runner")
    def test_node_syntax(self):
        result = subprocess.run(["node", "--check", str(SCRIPT)],
                                capture_output=True, text=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main()
