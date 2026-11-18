"""M0.8b-2a model-free tests: read-only OpenCode discovery under rootless Podman."""
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))

from isolated_opencode_models_probe import SCRIPT, REQUIRED, checks_pass, command_for


class ModelDiscoveryTests(unittest.TestCase):
    def test_command_forbids_host_network_and_source_mounts(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            private = root / "private"
            private.mkdir()
            sentinel = root / "host-not-mounted"
            sentinel.write_text("secret", encoding="utf-8")
            cmd = command_for("a" * 64, 1000, 1000, private, "qwen3.5:4b", sentinel)
        for flag in (
            "--pull=never", "--network=none", "--read-only", "--cap-drop=all",
            "--security-opt=no-new-privileges", "--userns=keep-id",
            "--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup",
            "--entrypoint=node", "--workdir=/tmp",
            "--pids-limit=96", "--memory=2g",
        ):
            self.assertIn(flag, cmd)
        self.assertEqual(cmd.count("--volume"), 1)
        self.assertTrue(cmd[cmd.index("--volume") + 1].endswith(":/run/codingassistant:ro"))
        self.assertNotIn("--privileged", cmd)
        self.assertNotIn("--network=host", cmd)
        self.assertNotIn("--build", cmd)
        self.assertEqual(cmd[cmd.index("--entrypoint=node") + 1], "a" * 64)
        self.assertEqual(cmd[-2], "qwen3.5:4b")

    def test_mutable_ids_and_foreign_model_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            private = root / "private"
            private.mkdir()
            sentinel = root / "outside"
            sentinel.touch()
            for image in ("runtime:latest", "a" * 12, "sha256:invalid", ""):
                with self.subTest(image=image), self.assertRaises(ValueError):
                    command_for(image, 1000, 1000, private, "qwen3.5:4b", sentinel)
            for model in ("ollama/qwen3.5:4b", "qwen3.5:4b\n", "", "../qwen:4b"):
                with self.subTest(model=model), self.assertRaises(ValueError):
                    command_for("a" * 64, 1000, 1000, private, model, sentinel)

    def test_fail_closed_exact_positive_check_set(self):
        success = {"checks": {key: True for key in REQUIRED}}
        self.assertTrue(checks_pass(success))
        for key in REQUIRED:
            modified = {"checks": dict(success["checks"])}
            modified["checks"][key] = False
            self.assertFalse(checks_pass(modified), key)
        self.assertFalse(checks_pass({"checks": {}}))
        self.assertFalse(checks_pass({"checks": {**success["checks"], "extra": True}}))

    def test_cli_invocation_only_models_not_run(self):
        text = SCRIPT.read_text(encoding="utf-8")
        self.assertIn('spawn("opencode", ["models"]', text)
        self.assertNotIn('spawn("opencode", ["run"]', text)
        self.assertNotIn('opencode run', text)
        self.assertIn("OPENCODE_CONFIG_CONTENT:", text)
        self.assertIn('server.listen(0, "127.0.0.1"', text)
        self.assertIn('"/v1/models"', text)
        self.assertIn("checks.no_unexpected_cli_route = false", text)
        self.assertIn('checks.unsupported_routes_denied = denied;', text)
        self.assertIn('outbound_tcp_denied', text)

    @unittest.skipUnless(shutil.which("node"), "Node is not on the CI test host")
    def test_node_client_syntax(self):
        result = subprocess.run(["node", "--check", str(SCRIPT)],
                                capture_output=True, text=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main()
