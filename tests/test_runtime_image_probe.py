"""M0.7 image build and offline-runtime contracts; no Podman in CI."""
import pathlib
import sys
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))
from runtime_image_probe import (
    CONTEXT, IMAGE, VERSION_PIN, build_command, run_command, check_versions,
    parse_versions, toolchain_failure
)

class OfflineToolchainTests(unittest.TestCase):
    def test_only_dedicated_build_context_is_used(self):
        cmd = build_command()
        self.assertEqual(cmd[:2], ["podman", "build"])
        self.assertIn("--pull=always", cmd)
        self.assertEqual(cmd[-1], str(CONTEXT))
        self.assertEqual(cmd[cmd.index("--file") + 1], str(CONTEXT / "Containerfile"))
        self.assertEqual(cmd[cmd.index("--tag") + 1], IMAGE)
        self.assertNotIn(str(ROOT), cmd)

    def test_containerfile_uses_pinned_opencode_and_supported_base(self):
        recipe = (CONTEXT / "Containerfile").read_text(encoding="utf-8")
        self.assertIn("mcr.microsoft.com/dotnet/sdk:10.0-noble", recipe)
        self.assertIn("docker.io/library/node:22-bookworm-slim", recipe)
        self.assertIn(f"ARG OPENCODE_VERSION={VERSION_PIN}", recipe)
        self.assertIn('OPENCODE_DISABLE_AUTOUPDATE=1', recipe)
        self.assertIn("USER 65532:65532", recipe)
        self.assertNotIn("ADD http", recipe)

    def test_container_context_excludes_secrets(self):
        context = (CONTEXT / ".containerignore").read_text(encoding="utf-8")
        self.assertIn("**", context)
        self.assertIn("!Containerfile", context)

    def test_runtime_does_not_mount_host_or_network(self):
        id_ = "sha256:" + "a" * 64
        cmd = run_command(id_, 1000, 1000)
        self.assertIn("--network=none", cmd)
        self.assertIn("--read-only", cmd)
        self.assertIn("--cap-drop=all", cmd)
        self.assertIn("--security-opt=no-new-privileges", cmd)
        self.assertIn("--pull=never", cmd)
        self.assertIn("--userns=keep-id", cmd)
        self.assertNotIn("--volume", cmd)
        self.assertNotIn("--privileged", cmd)
        self.assertNotIn("--network=host", cmd)
        self.assertIn(id_, cmd)
        self.assertIn("HOME=/tmp", cmd)
        self.assertEqual(cmd[-3], id_)
        self.assertEqual(cmd[-2], "-c")

    def test_bare_64_hex_podman_image_id_accepted(self):
        # Podman's inspect .Id is normally bare hex, not sha256:-prefixed.
        image_id = "a" * 64
        argv = run_command(image_id, 1000, 1000)
        self.assertEqual(argv[-3], image_id)
        self.assertEqual(argv.count(image_id), 1)
        self.assertIn("--pull=never", argv)
        self.assertIn("--network=none", argv)

    def test_prefixed_64_hex_image_id_accepted(self):
        image_id = "sha256:" + "B" * 64
        argv = run_command(image_id, 1000, 1000)
        self.assertEqual(argv[-3], image_id)

    def test_mutable_image_tag_rejected(self):
        for invalid in (IMAGE, "sha256:bad", "a" * 12, "a" * 63,
                        "a" * 65, "sha256:" + "g" * 64,
                        " sha256:" + "a" * 64, ""):
            with self.subTest(image_id=invalid), self.assertRaises(ValueError):
                run_command(invalid, 1000, 1000)

    def test_podman_launch_failure_not_reported_as_six_missing_tools(self):
        failure = toolchain_failure(125, {}, "", "Error: container start denied")
        self.assertIn("could not start", failure["reason"])
        self.assertIn("exit 125", failure["reason"])
        self.assertEqual(failure["stderr_tail"], "Error: container start denied")

    def test_tool_script_failure_distinguished_from_launch_failure(self):
        failure = toolchain_failure(2, {"dotnet": "10.0.404"}, "dotnet=10.0.404\n", "permission denied")
        self.assertIn("failed partway", failure["reason"])
        self.assertEqual(failure["stdout_tail"], "dotnet=10.0.404\n")

    def test_version_mismatch_reports_error_without_tool_absence_claim(self):
        failure = toolchain_failure(0, {"dotnet": "10.0.404"}, "dotnet=10.0.404\n", "")
        self.assertIn("Version output", failure["reason"])

    def test_diagnostics_are_bounded(self):
        failure = toolchain_failure(125, {}, "a" * 5000, "b" * 5000)
        self.assertEqual(len(failure["stdout_tail"]), 1200)
        self.assertEqual(len(failure["stderr_tail"]), 1200)

    def test_versions_parse_and_accept_matching_toolchain(self):
        sample = "dotnet=10.0.101\nnode=v22.22.1\nnpm=10.9.3\ngit=git version 2.48\nopencode=opencode v2.3.0\nrun_help=ok\n"
        values = parse_versions(sample)
        self.assertEqual(len(values), 6)
        self.assertTrue(all(check_versions(values).values()))

    def test_mismatch_must_fail_closed(self):
        values = parse_versions("dotnet=10.0.101\nnode=v22.22.1\nnpm=10.9.3\ngit=git version 2.48\nopencode=2.3.0\nrun_help=ok\n")
        for key in list(values):
            modified = dict(values)
            modified[key] = "wrong"
            self.assertFalse(all(check_versions(modified).values()), key)
        self.assertFalse(all(check_versions({}).values()))

if __name__ == "__main__":
    unittest.main()
