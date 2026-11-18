"""Model-free tests for the Linux-only local Ollama IPC transport."""
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "tools" / "m0"))
from bridge_probe import REQUIRED, command_for, evaluate
from sandbox_probe import DEFAULT_IMAGE


class BridgeCommandTests(unittest.TestCase):
    def test_linux_isolation_arguments_are_not_weakened(self):
        cmd = command_for(DEFAULT_IMAGE, pathlib.Path("/tmp/work"),
                          pathlib.Path("/tmp/secret-bridge"), pathlib.Path("/tmp/outside"), 1000, 1000)
        self.assertIn("--network=none", cmd)
        self.assertIn("--read-only", cmd)
        self.assertIn("--cap-drop=all", cmd)
        self.assertIn("--security-opt=no-new-privileges", cmd)
        self.assertNotIn("--network=host", cmd)
        self.assertNotIn("--privileged", cmd)
        self.assertEqual(cmd.count("--volume"), 2)
        self.assertIn(f"{pathlib.Path('/tmp/secret-bridge').resolve()}:/run/codingassistant:ro", cmd)
        self.assertIn(f"{pathlib.Path('/tmp/work').resolve()}:/workspace:rw", cmd)

    def test_all_required_pass_with_loopback(self):
        output = {"checks": {k: True for k in REQUIRED}, "loopback_tcp_available": True}
        self.assertEqual(evaluate(output)["status"], "PRECHECK_PASS")

    def test_all_required_without_loopback_reports_partial(self):
        output = {"checks": {k: True for k in REQUIRED}, "loopback_tcp_available": False}
        self.assertEqual(evaluate(output)["status"], "BRIDGE_ONLY")

    def test_fail_closed_for_missing_or_false_fields(self):
        self.assertEqual(evaluate({"checks": {}})["status"], "BLOCKED")
        for broken in REQUIRED:
            values = {k: True for k in REQUIRED}
            values[broken] = False
            self.assertEqual(evaluate({"checks": values})["status"], "BLOCKED", broken)

    def test_extra_fields_not_trusted(self):
        values = {k: True for k in REQUIRED}
        values["unexpected"] = True
        self.assertEqual(evaluate({"checks": values})["status"], "BLOCKED")


if __name__ == "__main__":
    unittest.main()
