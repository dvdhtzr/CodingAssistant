"""M0.6 contract tests; no Podman or Ollama needed in CI."""
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "tools" / "m0"))
from inference_transport_probe import REQUIRED, assess, command_for
from sandbox_probe import DEFAULT_IMAGE


class ProbeContractTests(unittest.TestCase):
    def test_all_required_checks_must_be_real_boolean_true(self):
        good = {"checks": {key: True for key in REQUIRED}}
        self.assertTrue(assess(good))
        self.assertFalse(assess({"checks": {}}))
        for key in REQUIRED:
            invalid = {item: True for item in REQUIRED}
            invalid[key] = False
            self.assertFalse(assess({"checks": invalid}), key)
            invalid[key] = "true"
            self.assertFalse(assess({"checks": invalid}), key)
        with_extra = {key: True for key in REQUIRED}
        with_extra["extra"] = True
        self.assertFalse(assess({"checks": with_extra}))

    def test_podman_remains_offline_rootless_and_capability_free(self):
        cmd = command_for(
            DEFAULT_IMAGE, pathlib.Path("/tmp/ca-work"),
            pathlib.Path("/tmp/ca-private"), pathlib.Path("/tmp/unmounted"), "qwen3.5:4b",
            1000, 1000)
        for constraint in ("--network=none", "--read-only", "--cap-drop=all",
                           "--security-opt=no-new-privileges", "--pull=never",
                           "--userns=keep-id", "--user", "1000:1000"):
            self.assertIn(constraint, cmd)
        self.assertNotIn("--privileged", cmd)
        self.assertNotIn("--network=host", cmd)
        self.assertEqual(cmd.count("--volume"), 2)
        self.assertIn(f"{pathlib.Path('/tmp/ca-private').resolve()}:/run/codingassistant:ro", cmd)
        self.assertIn(f"{pathlib.Path('/tmp/ca-work').resolve()}:/workspace:rw", cmd)
        self.assertNotIn("--publish", cmd)
        self.assertNotIn("--device", cmd)

    def test_container_fixed_script_not_model_payload(self):
        cmd = command_for(DEFAULT_IMAGE, pathlib.Path("/tmp/work"),
                          pathlib.Path("/tmp/private"), pathlib.Path("/tmp/out"), "qwen3.5:4b", 1, 1)
        self.assertIn("python3", cmd)
        self.assertIn("-c", cmd)
        self.assertIn("/run/codingassistant/ollama.sock", cmd)
        self.assertEqual(cmd[-2], "qwen3.5:4b")


if __name__ == "__main__":
    unittest.main()
