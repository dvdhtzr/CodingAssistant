"""M1.6 actual method/adapter discovery contract; Podman fully mocked.

Mocks cannot establish real Gateway RPC compatibility; owner Dell run will.
No agent, runtime subprocess, inference or network is used in tests.
"""
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
    "acpx_opencode_rpc_probe", ROOT / "tools/m1/acpx_opencode_rpc_probe.py")
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)

def done(argv, status=0, stdout="", stderr=""):
    return subprocess.CompletedProcess(argv, status, stdout, stderr)


def agent_report(installation="missing"):
    return {"agents": [{"id": "opencode", "runtimeId": "acp-opencode",
                         "installation": installation, "name": "OpenCode",
                         "enabled": True}]}


def registry_report(installation="missing"):
    return {"id": "opencode", "installation": installation,
            "expectedCommand": True}


class ReadonlyAcpxRpcTest(unittest.TestCase):
    def test_registry_is_only_offline_node_and_cannot_spawn(self):
        cmd = p.registry_command(p.IMAGE_ID, 1000, 1000)
        for flag in ("--network=none", "--read-only", "--cap-drop=all",
                     "--security-opt=no-new-privileges", "--pull=never",
                     "--user=1000:1000", "--entrypoint=node"):
            self.assertIn(flag, cmd)
        self.assertEqual(cmd[cmd.index("--entrypoint=node")+1], p.IMAGE_ID)
        self.assertEqual(cmd[-2], "-e")
        js = cmd[-1]
        self.assertIn("createAgentRegistry().inspect('opencode')", js)
        self.assertIn("createRequire('/app/dist/extensions/acpx/package.json')", js)
        self.assertIn("launch.kind", js)
        self.assertNotIn("child_process", js)
        self.assertNotIn("spawn(", js)
        self.assertNotIn("exec(", js)
        for forbidden in ("--privileged", "--publish", "--volume", "--mount"):
            self.assertNotIn(forbidden, cmd)
        with self.assertRaises(ValueError):
            p.registry_command("x" * 64, 1000, 1000)
        with self.assertRaises(ValueError):
            p.registry_command(p.IMAGE_ID, 0, 1000)

    def test_gateway_cli_rpc_is_single_readonly_plugin_method(self):
        cmd = p.rpc_command("ca-m15-acpx-sample")
        self.assertEqual(cmd, [
            "podman", "exec", "--interactive=false", "ca-m15-acpx-sample",
            "node", "/app/openclaw.mjs", "gateway", "call",
            "acpx.agents.list", "--params", "{}", "--json",
            "--timeout", "15000"])
        self.assertNotIn("--token", cmd)
        self.assertNotIn("npx", cmd)
        self.assertNotIn("sessions_spawn", cmd)
        for name in ("evil-container", "ca-m15-acpx-evil"):
            with self.assertRaises(ValueError):
                p.rpc_command(name)

    def test_safe_json_schema_rejects_unexpected_agent_data(self):
        assert p.parse_registry(json.dumps(registry_report())) == registry_report()
        for item in (
            dict(registry_report(), expectedCommand=False),
            dict(registry_report(), installation="package-exec"),
            dict(registry_report(), expectedCommand="true"),
            {"status": "missing"},
        ):
            with self.subTest(item=item), self.assertRaises(ValueError):
                p.parse_registry(json.dumps(item))
        result=p.parse_rpc(json.dumps(agent_report()))
        self.assertEqual(result["installation"], "missing")
        self.assertTrue(result["enabled"])
        for result in (
            {"agents": []},
            {"agents": [*agent_report()["agents"]]*2},
            {"agents": [dict(agent_report()["agents"][0],
                             installation="package-exec")]},
            {"agents": [dict(agent_report()["agents"][0], enabled="true")]},
            {"agents": [dict(agent_report()["agents"][0], runtimeId="other")]},
            {"agents": "not-list"},
            dict(agent_report(), other="secret"),
        ):
            with self.subTest(result=result), self.assertRaises(ValueError):
                p.parse_rpc(json.dumps(result))

    @mock.patch.object(p.sys, "platform", "linux")
    @mock.patch.object(p.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getgid", return_value=1000, create=True)
    def test_readonly_success_records_real_rpc_provenance_not_acp(self, *_mocks):
        calls = []

        def fixed(argv, timeout=50):
            calls.append(argv)
            if argv[1] == "run":
                if "--input-type=module" in argv:
                    return done(argv, stdout=json.dumps(registry_report()))
                return done(argv, stdout="container-id")
            if argv[1] == "inspect":
                return done(argv, stdout="true")
            if argv[1] == "exec":
                if "gateway" in argv:
                    self.assertIn("acpx.agents.list", argv)
                    return done(argv, stdout=json.dumps(agent_report()))
                return done(argv, stdout=json.dumps({
                    "/healthz": 200, "/startupz": 200, "/readyz": 200}))
            if argv[1] == "rm":
                return done(argv)
            if argv[1:3] == ["container", "exists"]:
                return done(argv, status=1)
            raise AssertionError(argv)

        with (mock.patch.object(p, "verify_prerequisites"),
              mock.patch.object(p, "inspect_derived", return_value=p.IMAGE_ID),
              mock.patch.object(p, "verify_offline", return_value={
                  "acpx_metadata": {"manifest": "acpx"}}),
              mock.patch.object(p, "fixed", side_effect=fixed)):
            result = p.run()
        self.assertEqual(result["status"], "ACPX_OPENCODE_DISCOVERY_PASS", result)
        self.assertEqual(result["agent"]["installation"], "missing")
        self.assertTrue(result["rpc_verified"])
        self.assertTrue(result["gateway_removed"])
        self.assertFalse(result["agent_spawned"])
        self.assertFalse(result["acp_session_started"])
        self.assertFalse(result["model_inference"])
        self.assertEqual(sum(x[1] == "run" for x in calls), 2)
        self.assertEqual(sum(x[1] == "rm" for x in calls), 1)

    @mock.patch.object(p.sys, "platform", "linux")
    @mock.patch.object(p.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getgid", return_value=1000, create=True)
    def test_gateway_registry_disagreement_blocks_even_if_rpc_returns(self, *_mocks):
        def fixed(argv, timeout=50):
            if argv[1] == "run":
                if "--input-type=module" in argv:
                    return done(argv, stdout=json.dumps(registry_report("missing")))
                return done(argv, stdout="container-id")
            if argv[1] == "inspect":
                return done(argv, stdout="true")
            if argv[1] == "exec":
                if "gateway" in argv:
                    return done(argv, stdout=json.dumps(agent_report("installed")))
                return done(argv, stdout=json.dumps({
                    "/healthz": 200, "/startupz": 200, "/readyz": 200}))
            if argv[1] == "rm":
                return done(argv)
            if argv[1:3] == ["container", "exists"]:
                return done(argv, status=1)
            raise AssertionError(argv)
        with (mock.patch.object(p, "verify_prerequisites"),
              mock.patch.object(p, "inspect_derived", return_value=p.IMAGE_ID),
              mock.patch.object(p, "verify_offline", return_value={
                  "acpx_metadata": {"manifest": "acpx"}}),
              mock.patch.object(p, "fixed", side_effect=fixed)):
            result = p.run()
        self.assertEqual(result["status"], "BLOCKED", result)
        self.assertIn("disagree", result["reason"])
        self.assertTrue(result["gateway_removed"])

    @mock.patch.object(p.sys, "platform", "win32")
    def test_windows_never_starts_runtime(self):
        with mock.patch.object(p, "fixed") as f:
            result = p.run()
        self.assertEqual(result["status"], "BLOCKED")
        f.assert_not_called()

    @mock.patch.object(p.sys, "platform", "linux")
    @mock.patch.object(p.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getgid", return_value=1000, create=True)
    def test_image_mismatch_never_starts_guest(self, *_mocks):
        with (mock.patch.object(p, "verify_prerequisites"),
              mock.patch.object(p, "inspect_derived", return_value="e"*64),
              mock.patch.object(p, "fixed") as called):
            result = p.run()
        self.assertEqual(result["status"], "BLOCKED")
        self.assertFalse(result["gateway_started"])
        self.assertFalse(result["rpc_verified"])
        self.assertFalse(result["agent_spawned"])
        self.assertFalse(any(args[0][1] == "run" for args in
                             (c.args for c in called.call_args_list)))
if __name__ == "__main__":
    unittest.main()
