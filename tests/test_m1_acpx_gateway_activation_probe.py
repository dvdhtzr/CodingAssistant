"""M1.5 sealed ACPX lazy registration test contracts: no real containers/models."""
from __future__ import annotations
import importlib.util
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools/m1"))
spec = importlib.util.spec_from_file_location(
    "acpx_gateway_activation_probe",
    ROOT / "tools/m1/acpx_gateway_activation_probe.py",
)
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)


def completed(args, status=0, output="", err=""):
    return subprocess.CompletedProcess(args, status, output, err)


def fixed_stub(actions: list[list[str]], *, has_log=True, ready=True,
               cleanup=True):
    def fake(args, timeout=50):
        actions.append(args)
        action = args[1]
        if action == "run":
            return completed(args, output="disposable-id")
        if action == "inspect":
            return completed(args, output="true")
        if action == "exec":
            return completed(args, output=(
                '{"\/healthz":200,"\/startupz":200,"\/readyz":200}' if ready else
                '{"\/healthz":200,"\/startupz":200,"\/readyz":503}'))
        if action == "logs":
            return completed(
                args, output=(
                    "OpenClaw Gateway: embedded acpx runtime backend registered lazily\n"
                    if has_log else "OpenClaw Gateway booted\n"))
        if action == "rm":
            return completed(args)
        if action == "container" and args[2] == "exists":
            return completed(args, status=1 if cleanup else 0)
        raise AssertionError("Unexpected Podman process: " + repr(args))
    return fake


class AcpxGatewayActivationTests(unittest.TestCase):
    def test_no_mounts_or_network_or_agents_and_exact_source_image(self):
        with tempfile.TemporaryDirectory() as d:
            f = pathlib.Path(d) / "gateway.env"
            f.write_text("OPENCLAW_GATEWAY_TOKEN=test\n", encoding="utf-8")
            f.chmod(0o600)
            argv = p.gateway_command(p.IMAGE_ID, "ca-m15-acpx-12345", 1000, 1000, f)
        for key in (
            "--network=none", "--read-only", "--cap-drop=all",
            "--security-opt=no-new-privileges", "--userns=keep-id",
            "--user=1000:1000", "--pids-limit=96", "--memory=2g",
            "--no-healthcheck", "--pull=never",
            "--env=OPENCLAW_SKIP_ACPX_RUNTIME_PROBE=1",
            "--env=OPENCLAW_ACPX_RUNTIME_STARTUP_PROBE=0",
            "--env=OPENCLAW_NO_RESPAWN=1",
        ):
            self.assertIn(key, argv)
        for forbidden in (
            "--volume", "--mount", "--publish", "--privileged",
            "--network=host", "--device", "--model", "acp",
        ):
            self.assertNotIn(forbidden, argv)
        self.assertNotIn("OPENCLAW_SKIP_ACPX_RUNTIME=1", argv)
        self.assertEqual(argv[argv.index("--entrypoint=node")+1], p.IMAGE_ID)
        self.assertEqual(argv[-7:], [
            "/app/openclaw.mjs", "gateway", "--allow-unconfigured",
            "--bind", "loopback", "--port", "18789",
        ])
        with tempfile.TemporaryDirectory() as d:
            f = pathlib.Path(d) / "gateway.env"
            f.write_text("x", encoding="utf-8")
            f.chmod(0o600)
            for args in (
                ("f"*64, "ca-m15-acpx-abcdef", 1000, 1000),
                (p.IMAGE_ID, "bad-name", 1000, 1000),
                (p.IMAGE_ID, "ca-m15-acpx-abcdef", 0, 1000),
            ):
                with self.subTest(args=args), self.assertRaises(ValueError):
                    p.gateway_command(*args, f)
            if p.os.name == "posix":
                f.chmod(0o644)
                with self.assertRaises(ValueError):
                    p.gateway_command(
                        p.IMAGE_ID, "ca-m15-acpx-abcdef", 1000, 1000, f)

    @mock.patch.object(p.sys, "platform", "linux")
    @mock.patch.object(p.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getgid", return_value=1000, create=True)
    def test_true_lazy_registration_still_does_not_call_acp(self, *_mocks):
        calls = []
        with (mock.patch.object(p, "verify_prerequisites"),
              mock.patch.object(p, "inspect_derived", return_value=p.IMAGE_ID),
              mock.patch.object(p, "verify_offline", return_value={
                  "acpx_metadata": {"manifest": "acpx"}}),
              mock.patch.object(p, "fixed", side_effect=fixed_stub(calls))):
            result = p.run()
        self.assertEqual(result["status"], "ACPX_LAZY_REGISTRATION_PASS", result)
        self.assertTrue(result["acpx_lazy_registration_observed"])
        self.assertTrue(result["gateway_removed"])
        self.assertTrue(result["offline_image_rechecked"])
        self.assertEqual(sum(x[1] == "run" for x in calls), 1)
        self.assertEqual(sum(x[1] == "rm" for x in calls), 1)
        self.assertFalse(result["acp_session_started"])
        self.assertFalse(result["adapter_spawn_requested"])
        self.assertFalse(any("--publish" in c or "--volume" in c for c in calls))

    @mock.patch.object(p.sys, "platform", "linux")
    @mock.patch.object(p.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getgid", return_value=1000, create=True)
    def test_healthy_gateway_without_acpx_is_not_success(self, *_mocks):
        calls = []
        with (mock.patch.object(p, "verify_prerequisites"),
              mock.patch.object(p, "inspect_derived", return_value=p.IMAGE_ID),
              mock.patch.object(p, "verify_offline", return_value={
                  "acpx_metadata": {"manifest": "acpx"}}),
              mock.patch.object(p, "fixed", side_effect=fixed_stub(
                  calls, has_log=False))):
            result = p.run()
        self.assertEqual(result["status"], "BLOCKED")
        self.assertEqual(result["health_codes"],
                         {"/healthz":200,"/startupz":200,"/readyz":200})
        self.assertFalse(result["acpx_lazy_registration_observed"])
        self.assertTrue(result["gateway_removed"])

    @mock.patch.object(p.sys, "platform", "linux")
    @mock.patch.object(p.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getgid", return_value=1000, create=True)
    def test_failure_to_remove_disposable_gateway_blocks(self, *_mocks):
        calls = []
        with (mock.patch.object(p, "verify_prerequisites"),
              mock.patch.object(p, "inspect_derived", return_value=p.IMAGE_ID),
              mock.patch.object(p, "verify_offline", return_value={
                  "acpx_metadata": {"manifest": "acpx"}}),
              mock.patch.object(p, "fixed", side_effect=fixed_stub(
                  calls, cleanup=False))):
            result = p.run()
        self.assertEqual(result["status"], "BLOCKED")
        self.assertFalse(result["gateway_removed"])
        self.assertIn("cleanup", result["reason"])

    @mock.patch.object(p.sys, "platform", "linux")
    @mock.patch.object(p.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getgid", return_value=1000, create=True)
    def test_wrong_derived_image_never_starts_gateway(self, *_mocks):
        calls = []
        with (mock.patch.object(p, "verify_prerequisites"),
              mock.patch.object(p, "inspect_derived", return_value="f"*64),
              mock.patch.object(p, "fixed", side_effect=fixed_stub(calls))):
            result = p.run()
        self.assertEqual(result["status"], "BLOCKED")
        self.assertFalse(result["gateway_started"])
        self.assertFalse(any(c[1] == "run" for c in calls))

    @mock.patch.object(p.sys, "platform", "win32")
    def test_windows_qualification_not_implied(self):
        with mock.patch.object(p, "fixed") as invoked:
            result = p.run()
        self.assertEqual(result["status"], "BLOCKED")
        invoked.assert_not_called()


if __name__ == "__main__":
    unittest.main()
