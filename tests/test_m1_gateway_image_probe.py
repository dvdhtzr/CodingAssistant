"""M1.1 pinned-image rootless discovery tests (all Podman calls mocked)."""
from __future__ import annotations

import importlib.util
import json
import pathlib
import subprocess
import sys
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools/m1"))
spec = importlib.util.spec_from_file_location(
    "gateway_image_probe", ROOT / "tools/m1/gateway_image_probe.py")
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)

ID = "a" * 64
DIGEST = probe.APPROVED_REPO_DIGEST.split("sha256:")[1]
INFO = [{"Id": "sha256:" + ID,
         "RepoDigests": ["ghcr.io/openclaw/openclaw@sha256:" + DIGEST]}]


def completed(argv, status=0, output="", err=""):
    return subprocess.CompletedProcess(argv, status, output, err)


class GatewayImageContracts(unittest.TestCase):
    def test_container_command_is_fixed_no_network_no_mounts(self):
        for args in (("--version",), ("/app/openclaw.mjs", "--version")):
            cmd = probe.guest_command(ID, 1000, 1000, args)
            for flag in (
                "--pull=never", "--network=none", "--read-only",
                "--cap-drop=all", "--security-opt=no-new-privileges",
                "--userns=keep-id", "--user=1000:1000",
                "--pids-limit=64", "--memory=2g",
                "--entrypoint=node", "--workdir=/app",
            ):
                self.assertIn(flag, cmd)
            self.assertNotIn("--volume", cmd)
            self.assertNotIn("--publish", cmd)
            self.assertNotIn("--privileged", cmd)
            self.assertEqual(cmd[-len(args):], list(args))
            self.assertEqual(cmd[cmd.index("--entrypoint=node")+1], ID)
        for bad in (
            ("gateway",), ("sh", "-c", "touch /tmp/example"),
            ("/app/openclaw.mjs", "gateway"), (),
        ):
            with self.subTest(args=bad):
                with self.assertRaises(ValueError):
                    probe.guest_command(ID, 1000, 1000, bad)
        with self.assertRaises(ValueError):
            probe.guest_command("latest", 1000, 1000, ("--version",))

    def test_official_digest_must_be_pinned(self):
        with mock.patch.object(probe, "fixed",
                               return_value=completed([], output=json.dumps(INFO))) as process:
            image = probe.inspected_image()
        process.assert_called_once_with(["podman", "image", "inspect",
                                         probe.APPROVED_REPO_DIGEST])
        self.assertEqual(image["image_id"], ID)
        self.assertEqual(image["repo_digest"],
                         "ghcr.io/openclaw/openclaw@sha256:" + DIGEST)
        for bad in (
            [{"Id": "sha256:" + ID, "RepoDigests": []}],
            [{"Id": "sha256:" + ID, "RepoDigests": [
                "ghcr.io/openclaw/openclaw@sha256:" + "b" * 64]}],
            [{"Id": "sha256:" + ID, "RepoDigests": [
                probe.APPROVED_REPO_DIGEST, None]}],
            [{"Id": "sha256:" + ID,
              "RepoDigests": ["docker.io/untrusted/openclaw@sha256:" + DIGEST]}],
            [{"Id": "notsha", "RepoDigests": [
                "ghcr.io/openclaw/openclaw@sha256:" + DIGEST]}],
            [],
        ):
            with mock.patch.object(probe, "fixed",
                                   return_value=completed([], output=json.dumps(bad))):
                with self.assertRaises(ValueError):
                    probe.inspected_image()

    @mock.patch.object(probe.sys, "platform", "linux")
    @mock.patch.object(probe.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(probe.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(probe.os, "getgid", return_value=1000, create=True)
    def test_preparation_is_opt_in_and_runtime_does_not_start_gateway(self, *_platform_mocks):
        calls = []

        def fake(argv, timeout=50):
            calls.append(tuple(argv))
            if argv[1] == "info":
                return completed(argv, output="true\n")
            if argv[1] == "pull":
                return completed(argv, output="pulled")
            if argv[1] == "image":
                return completed(argv, output=json.dumps(INFO))
            if argv[1] == "run":
                if argv[-1] == "--version" and argv[-2] == ID:
                    return completed(argv, output="v24.22.1\n")
                if argv[-1] == "--version" and argv[-2] == "/app/openclaw.mjs":
                    return completed(argv, output="OpenClaw 2026.11.5\n")
            raise AssertionError("Unexpected Podman route " + repr(argv))

        with mock.patch.object(probe, "fixed", side_effect=fake):
            first = probe.probe(prepare=False)
        self.assertEqual(first["status"], "RUNTIME_IMAGE_PASS", first)
        self.assertFalse(first["pull_requested"])
        self.assertFalse(any(cmd[1] == "pull" for cmd in calls))
        self.assertEqual(sum(cmd[1] == "run" for cmd in calls), 2)
        for cmd in calls:
            self.assertNotIn("gateway", cmd)
            self.assertNotIn("acp", cmd)
        calls.clear()
        with mock.patch.object(probe, "fixed", side_effect=fake):
            with_pull = probe.probe(prepare=True)
        self.assertEqual(with_pull["status"], "RUNTIME_IMAGE_PASS", with_pull)
        self.assertEqual(sum(cmd[1] == "pull" for cmd in calls), 1)
        self.assertEqual(next(cmd for cmd in calls if cmd[1] == "pull"),
                         ("podman", "pull", probe.APPROVED_REPO_DIGEST))

    @mock.patch.object(probe.sys, "platform", "linux")
    @mock.patch.object(probe.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(probe.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(probe.os, "getgid", return_value=1000, create=True)
    def test_fails_closed_on_old_node_and_wrong_cli_version(self, *_platform_mocks):
        def fake(argv, timeout=50):
            if argv[1] == "info":
                return completed(argv, output="true")
            if argv[1] == "image":
                return completed(argv, output=json.dumps(INFO))
            if argv[1] == "run":
                if argv[-2] == ID:
                    return completed(argv, output="v22.22.1")
                return completed(argv, output="2026.11.5")
            raise AssertionError(argv)
        with mock.patch.object(probe, "fixed", side_effect=fake):
            report = probe.probe()
        self.assertEqual(report["status"], "BLOCKED")
        self.assertIn("Node", report["reason"])
        def fake_wrong(argv, timeout=50):
            if argv[1] == "run":
                if argv[-2] == ID:
                    return completed(argv, output="v24.22.1")
                return completed(argv, output="2026.11.4")
            return fake(argv, timeout)
        with mock.patch.object(probe, "fixed", side_effect=fake_wrong):
            report = probe.probe()
        self.assertEqual(report["status"], "BLOCKED")
        self.assertIn("differs", report["reason"])

    @mock.patch.object(probe.sys, "platform", "linux")
    @mock.patch.object(probe.os, "geteuid", return_value=1000, create=True)
    def test_wrong_registry_digest_never_runs_a_guest(self, *_platform_mocks):
        calls = []

        def fake(argv, timeout=50):
            calls.append(tuple(argv))
            if argv[1] == "info":
                return completed(argv, output="true")
            if argv[1] == "image":
                return completed(argv, output=json.dumps([{
                    "Id": "sha256:" + ID,
                    "RepoDigests": ["ghcr.io/openclaw/openclaw@sha256:" + "b" * 64],
                }]))
            raise AssertionError(argv)

        with mock.patch.object(probe, "fixed", side_effect=fake):
            report = probe.probe()
        self.assertEqual(report["status"], "BLOCKED")
        self.assertIn("exact approved", report["reason"])
        self.assertFalse(any(cmd[1] in ("pull", "run") for cmd in calls))

    def test_exact_digest_selected_even_if_other_digests_present(self):
        info = [{"Id": "sha256:" + ID, "RepoDigests": [
            "ghcr.io/openclaw/openclaw@sha256:" + "0" * 64,
            probe.APPROVED_REPO_DIGEST,
        ]}]
        with mock.patch.object(probe, "fixed",
                               return_value=completed([], output=json.dumps(info))):
            self.assertEqual(probe.inspected_image()["repo_digest"],
                             probe.APPROVED_REPO_DIGEST)

    @mock.patch.object(probe.sys, "platform", "linux")
    @mock.patch.object(probe.os, "geteuid", return_value=1000, create=True)
    def test_missing_image_does_not_implicitly_pull(self, *_platform_mocks):
        paths = []
        def fake(argv, timeout=50):
            paths.append(tuple(argv))
            if argv[1] == "info":
                return completed(argv, output="true")
            return completed(argv, status=125)
        with mock.patch.object(probe, "fixed", side_effect=fake):
            out = probe.probe()
        self.assertEqual(out["status"], "BLOCKED")
        self.assertIn("--prepare", out["reason"])
        self.assertFalse(any(cmd[1] in ("pull", "run") for cmd in paths))

    @mock.patch.object(probe.sys, "platform", "win32")
    def test_host_windows_does_not_execute_podman(self):
        with mock.patch.object(probe, "fixed") as process:
            status = probe.probe(prepare=True)
        self.assertEqual(status["status"], "BLOCKED")
        process.assert_not_called()


if __name__ == "__main__":
    unittest.main()
