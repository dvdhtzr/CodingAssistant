"""M1.2 Gateway safety and lifecycle contracts; Podman fully mocked."""
from __future__ import annotations
import importlib.util
import json
import os
import pathlib
import stat
import subprocess
import sys
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools/m1"))
spec = importlib.util.spec_from_file_location(
    "gateway_loopback_probe", ROOT / "tools/m1/gateway_loopback_probe.py")
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)


def completed(cmd, status=0, stdout=""):
    return subprocess.CompletedProcess(cmd, status, stdout, "")


class IsolatedGatewaySmokeTests(unittest.TestCase):
    def test_exact_guest_argv_isolated_and_no_privilege_escape(self):
        with __import__("tempfile").TemporaryDirectory() as temp:
            env = pathlib.Path(temp) / "token"
            env.write_text("OPENCLAW_GATEWAY_TOKEN=demo\n", encoding="utf-8")
            env.chmod(0o600)
            cmd = p.gateway_command(
                p.APPROVED_IMAGE_ID, "ca-m12-gateway-unit", 1000, 1000, env)
        for value in (
            "--network=none", "--read-only", "--cap-drop=all",
            "--security-opt=no-new-privileges", "--userns=keep-id",
            "--user=1000:1000", "--pull=never", "--no-healthcheck",
            "--entrypoint=node", "--memory=2g", "--pids-limit=96",
            "--allow-unconfigured", "loopback",
        ):
            self.assertIn(value, cmd)
        for forbidden in ("--privileged", "--volume", "--publish",
                          "--network=host", "--device", "--mount"):
            self.assertNotIn(forbidden, cmd)
        self.assertEqual(cmd[-7:], [
            "/app/openclaw.mjs", "gateway", "--allow-unconfigured",
            "--bind", "loopback", "--port", "18789",
        ])
        self.assertEqual(cmd[cmd.index("--entrypoint=node") + 1],
                         p.APPROVED_IMAGE_ID)
        self.assertNotIn("OPENAI_API_KEY", " ".join(cmd))

    def test_denies_mismatched_image_and_unprivate_token(self):
        with __import__("tempfile").TemporaryDirectory() as temp:
            path = pathlib.Path(temp) / "token"
            path.write_text("some-test", encoding="utf-8")
            path.chmod(0o600)
            with self.assertRaises(ValueError):
                p.gateway_command("b" * 64, "ca-m12-gateway-a", 1000, 1000, path)
            with self.assertRaises(ValueError):
                p.gateway_command(p.APPROVED_IMAGE_ID, "bad", 1000, 1000, path)
            path.chmod(0o644)
            if os.name == "posix":
                with self.assertRaises(ValueError):
                    p.gateway_command(
                        p.APPROVED_IMAGE_ID, "ca-m12-gateway-a", 1000, 1000, path)

    def test_health_probe_strict_schema(self):
        self.assertEqual(p.parse_codes(
            '{"\/healthz":200,"\/startupz":200,"\/readyz":200}'),
            {"/healthz": 200, "/startupz": 200, "/readyz": 200})
        for data in (
            '{"\/healthz":200,"\/startupz":200}',
            '{"\/healthz":200,"\/startupz":200,"\/readyz":204}',
            '{"\/healthz":200,"\/startupz":200,"\/readyz":true}',
            '{"\/healthz":200,"\/startupz":200,"\/readyz":200,"token":"x"}',
            'not-json',
        ):
            with self.assertRaises((ValueError, json.JSONDecodeError)):
                p.parse_codes(data)

    @mock.patch.object(p.sys, "platform", "linux")
    @mock.patch.object(p.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getgid", return_value=1000, create=True)
    def test_full_lifecycle_starts_and_always_removes(self, *_mocks):
        commands = []
        def fake(cmd, timeout=50):
            commands.append(cmd)
            if cmd[1] == "info":
                return completed(cmd, stdout="true\n")
            if cmd[1] == "run":
                return completed(cmd, stdout="containerid\n")
            if cmd[1] == "inspect":
                return completed(cmd, stdout="true\n")
            if cmd[1] == "exec":
                return completed(cmd, stdout=json.dumps(
                    {"/healthz": 200, "/startupz": 200, "/readyz": 200}))
            if cmd[1] == "rm":
                return completed(cmd)
            if cmd[1:3] == ["container", "exists"]:
                return completed(cmd, status=1)
            raise AssertionError(cmd)
        with (mock.patch.object(p, "fixed", side_effect=fake),
              mock.patch.object(p, "inspected_image",
                                return_value={"image_id": p.APPROVED_IMAGE_ID,
                                "repo_digest": p.APPROVED_REPO_DIGEST}),
              mock.patch.object(p.time, "sleep")):
            result = p.run()
        self.assertEqual(result["status"], "GATEWAY_LOOPBACK_PASS", result)
        self.assertTrue(result["gateway_removed"])
        self.assertTrue(any(cmd[1] == "rm" for cmd in commands))
        self.assertEqual(sum(cmd[1] == "run" for cmd in commands), 1)
        self.assertFalse(any("--publish" in cmd or "--volume" in cmd
                             for cmd in commands))

    @mock.patch.object(p.sys, "platform", "linux")
    @mock.patch.object(p.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getgid", return_value=1000, create=True)
    def test_unready_gateway_is_blocked_but_cleaned(self, *_mocks):
        calls = []
        def fake(cmd, timeout=50):
            calls.append(cmd)
            if cmd[1] == "info":
                return completed(cmd, stdout="true")
            if cmd[1] == "run":
                return completed(cmd, stdout="containerid")
            if cmd[1] == "inspect":
                return completed(cmd, stdout="true")
            if cmd[1] == "exec":
                return completed(cmd, stdout=json.dumps(
                    {"/healthz": 200, "/startupz": 200, "/readyz": 503}))
            if cmd[1] == "rm":
                return completed(cmd)
            if cmd[1:3] == ["container", "exists"]:
                return completed(cmd, status=1)
            raise AssertionError(cmd)
        with (mock.patch.object(p, "fixed", side_effect=fake),
              mock.patch.object(p, "inspected_image",
                                return_value={"image_id": p.APPROVED_IMAGE_ID,
                                "repo_digest": p.APPROVED_REPO_DIGEST}),
              mock.patch.object(p.time, "sleep")):
            result = p.run()
        self.assertEqual(result["status"], "BLOCKED")
        self.assertTrue(result["gateway_removed"])
        self.assertEqual(sum(cmd[1] == "exec" for cmd in calls), 30)

    @mock.patch.object(p.sys, "platform", "linux")
    @mock.patch.object(p.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getgid", return_value=1000, create=True)
    def test_wrong_image_never_starts_service(self, *_mocks):
        calls = []
        def fake(cmd, timeout=50):
            calls.append(cmd)
            if cmd[1] == "info":
                return completed(cmd, stdout="true")
            if cmd[1] == "container":
                return completed(cmd, status=1)
            return completed(cmd)
        with (mock.patch.object(p, "fixed", side_effect=fake),
              mock.patch.object(p, "inspected_image",
                                return_value={"image_id": "b" * 64,
                                "repo_digest": p.APPROVED_REPO_DIGEST})):
            result = p.run()
        self.assertEqual(result["status"], "BLOCKED")
        self.assertFalse(any(cmd[1] == "run" for cmd in calls))

    @mock.patch.object(p.sys, "platform", "win32")
    def test_windows_remains_unqualified(self):
        with mock.patch.object(p, "fixed") as proc:
            result = p.run()
        self.assertEqual(result["status"], "BLOCKED")
        proc.assert_not_called()


if __name__ == "__main__":
    unittest.main()
