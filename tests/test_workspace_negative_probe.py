"""M0.10 model-free regressions: immutable fixture, rooted mount, fail closed."""
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))
from workspace_negative_probe import CLIENT, REQUIRED, attack_command, checks_pass


class NegativeWorkspaceTests(unittest.TestCase):
    def test_same_podman_security_options_as_m09(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            workspace = root / "workspace"
            workspace.mkdir()
            sentinel = root / "outside"
            sentinel.write_text("unmounted")
            command = attack_command("b"*64, 1000, 1000, workspace, sentinel)
        for key in ("--pull=never", "--network=none", "--read-only",
                    "--cap-drop=all", "--security-opt=no-new-privileges",
                    "--userns=keep-id", "--pids-limit=64", "--memory=512m",
                    "--tmpfs=/tmp:rw,nosuid,nodev,size=64m,notmpcopyup"):
            self.assertIn(key, command)
        self.assertEqual(command.count("--volume"), 1)
        self.assertTrue(command[command.index("--volume") + 1].endswith(":/workspace:rw"))
        self.assertNotIn("--network=host", command)
        self.assertNotIn("--privileged", command)
        self.assertNotIn("--build", command)
        self.assertEqual(command[command.index("--entrypoint=node") + 1], "b"*64)
        self.assertIn("workspace_attack_client.mjs",
                      str(CLIENT))
        self.assertEqual(command[command.index("--eval") + 1],
                         CLIENT.read_text(encoding="utf-8"))
        for mutable in ("latest", "sha256:notahash", "b"*12):
            with self.subTest(image=mutable), tempfile.TemporaryDirectory() as t:
                p = pathlib.Path(t)
                work = p / "workspace"
                work.mkdir()
                secret = p / "host"
                secret.touch()
                with self.assertRaises(ValueError):
                    attack_command(mutable, 1000, 1000, work, secret)

    def test_every_negative_check_required(self):
        passing = {"checks": {key: True for key in REQUIRED}}
        self.assertTrue(checks_pass(passing))
        for key in REQUIRED:
            altered = dict(passing["checks"])
            altered[key] = False
            self.assertFalse(checks_pass({"checks": altered}), key)
            altered[key] = 1
            self.assertFalse(checks_pass({"checks": altered}), key)
        self.assertFalse(checks_pass({"checks": {}}))
        self.assertFalse(checks_pass({"checks": {**passing["checks"], "extra": True}}))

    def test_attempts_are_fixed_and_do_not_launch_agent(self):
        text = CLIENT.read_text(encoding="utf-8")
        for fragment in (
            "relative_traversal_write_denied",
            "symlink_outside_write_denied",
            "symlink_host_read_denied",
            "git_remote_connection_denied",
            "child_external_tcp_denied",
            "no_new_privileges",
            "host_sentinel_not_visible",
            'spawnSync("git", [',
            'spawnSync(process.execPath, ["-e", child, outside]',
            '"/workspace"',
        ):
            self.assertIn(fragment, text)
        for disallowed in ("opencode run", "ollama generate",
                           "npm install", "curl | sh", "sudo ",
                           '"/var/run/docker.sock"', '"--privileged"'):
            self.assertNotIn(disallowed, text)

    @unittest.skipUnless(shutil.which("node"), "Node unavailable")
    def test_node_syntax(self):
        p = subprocess.run(["node", "--check", str(CLIENT)],
                           capture_output=True, text=True, timeout=20)
        self.assertEqual(p.returncode, 0, p.stderr)


if __name__ == "__main__":
    unittest.main()
