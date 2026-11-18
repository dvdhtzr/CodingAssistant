"""M0 isolation contract, model-free tests; no Podman required in CI."""
import pathlib
import sys
import unittest
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "tools" / "m0"))
from sandbox_probe import command_for, checks_pass, DEFAULT_IMAGE

REQUIRED = (
    "non_root_identity", "no_effective_capabilities", "no_new_privileges",
    "root_filesystem_read_only", "external_network_denied",
    "unmounted_host_file_inaccessible", "workspace_write_available"
)

class RootlessSandboxContractTests(unittest.TestCase):
    def test_podman_command_has_enforced_boundaries(self):
        argv = command_for(DEFAULT_IMAGE, pathlib.Path("/tmp/m0-workspace"),
                           pathlib.Path("/tmp/m0-outside-sentinel"), 1000, 1000)
        self.assertEqual(argv[:2], ["podman", "run"])
        for required in ("--rm", "--pull=never", "--network=none", "--read-only",
                         "--cap-drop=all", "--security-opt=no-new-privileges",
                         "--userns=keep-id", "--user", "1000:1000"):
            self.assertIn(required, argv)
        self.assertIn("/tmp/m0-workspace:/workspace:rw", argv)
        self.assertEqual([x for x in argv if x.startswith("--volume")], ["--volume"])
        self.assertEqual(argv[-4], "python3")
        self.assertNotIn("--privileged", argv)
        self.assertNotIn("--network=host", argv)

    def test_fail_closed_for_incomplete_output(self):
        self.assertFalse(checks_pass({"checks": {"non_root_identity": True}}))
        self.assertFalse(checks_pass({}))

    def test_negative_check_failure_denies_precheck(self):
        values = {key: True for key in REQUIRED}
        values["external_network_denied"] = False
        self.assertFalse(checks_pass({"checks": values}))

    def test_unexpected_extra_check_blocks(self):
        values = {key: True for key in REQUIRED}
        values["network_allowed"] = True
        self.assertFalse(checks_pass({"checks": values}))

    def test_all_negative_checks_pass(self):
        self.assertTrue(checks_pass({"checks": {key: True for key in REQUIRED}}))

    def test_non_boolean_values_are_rejected(self):
        values = {key: True for key in REQUIRED}
        values["root_filesystem_read_only"] = "true"
        self.assertFalse(checks_pass({"checks": values}))

if __name__ == "__main__":
    unittest.main()
