"""M1.8 offline ACPX/OpenCode initialize contract; tests never spawn a model.

The live upstream initialization occurs on the owner Dell only.
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
sys.path.insert(0, str(ROOT / "tools/m0"))
spec = importlib.util.spec_from_file_location(
    "acpx_initialize_probe", ROOT / "tools/m1/acpx_initialize_probe.py"
)
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)
M0_ID = "a" * 64

def done(argv, status=0, out=""):
    return subprocess.CompletedProcess(argv, status, out, "")

def good():
    return {
        "ok": True, "protocolVersion": "1",
        "admitted": 1, "spawned": 1, "shutdown": True,
        "acpInitializeCompleted": True, "sessionCreated": False,
        "promptSent": False,
    }

class AcpxInitializeContract(unittest.TestCase):
    def test_only_exact_pinned_offline_nonroot_one_shot_guest(self):
        cmd = p.command_for(p.OWNER_IMAGE_ID, "ca-m18-init-012345abcdef", 1000, 1000)
        for flag in (
            "--pull=never", "--network=none", "--read-only", "--cap-drop=all",
            "--security-opt=no-new-privileges", "--userns=keep-id",
            "--user=1000:1000", "--pids-limit=96", "--memory=2g",
            "--cpus=2", "--entrypoint=node", "--env=HOME=/tmp",
            "--env=OPENCODE_DISABLE_AUTOUPDATE=1",
        ):
            self.assertIn(flag, cmd)
        for denied in (
            "--privileged", "--publish", "--mount", "--volume",
            "--network=host", "--device", "--env-file",
        ):
            self.assertNotIn(denied, cmd)
        self.assertEqual(cmd[-4:-2], ["--input-type=module", "-e"])
        self.assertEqual(cmd[cmd.index("--entrypoint=node") + 1], p.OWNER_IMAGE_ID)
        self.assertIn("instance.doctor()", cmd[-1])
        self.assertIn("await instance.shutdown()", cmd[-1])
        self.assertIn("overrides: {opencode: [expected, 'acp']}", cmd[-1])
        self.assertIn("agentProcessEnvAllow: []", cmd[-1])
        self.assertIn("permissionMode: 'deny-all'", cmd[-1])
        self.assertIn("nonInteractivePermissions: 'deny'", cmd[-1])
        self.assertIn("fs: false", cmd[-1])
        self.assertIn("terminal: false", cmd[-1])
        self.assertIn("launch.scope?.kind !== 'runtime-probe'", cmd[-1])
        self.assertNotIn("session/new", cmd[-1])
        self.assertNotIn("session/prompt", cmd[-1])
        self.assertNotIn("npx -y", cmd[-1])
        self.assertNotIn("http://", cmd[-1])
        with self.assertRaises(ValueError):
            p.command_for("f" * 64, "ca-m18-init-012345abcdef", 1000, 1000)
        for name in ("ca-m18-init-bad", "ca-m18-init-012345abcde_", "other"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                p.command_for(p.OWNER_IMAGE_ID, name, 1000, 1000)
        with self.assertRaises(ValueError):
            p.command_for(p.OWNER_IMAGE_ID, "ca-m18-init-012345abcdef", 0, 1000)

    def test_schema_requires_real_initialize_one_approved_child_shutdown(self):
        self.assertEqual(p.parse_result(json.dumps(good())),
                         {"protocol_version": "1", "admitted_process_count": 1,
                          "spawned_process_count": 1, "runtime_shutdown": True,
                          "acp_initialize": True})
        invalid = [
            {**good(), "ok": False},
            {**good(), "protocolVersion": None},
            {**good(), "protocolVersion": "../bad"},
            {**good(), "protocolVersion": "a" * 60},
            {**good(), "admitted": 2},
            {**good(), "spawned": 0},
            {**good(), "shutdown": False},
            {**good(), "acpInitializeCompleted": False},
            {**good(), "sessionCreated": True},
            {**good(), "promptSent": True},
            {**good(), "spawned": True},
            {**good(), "secret": "no"},
            [],
            "hello",
        ]
        for item in invalid:
            with self.subTest(item=item), self.assertRaises(ValueError):
                p.parse_result(json.dumps(item))
        with self.assertRaises(ValueError):
            p.parse_result("x" * 1801)

    @mock.patch.object(p.sys, "platform", "linux")
    @mock.patch.object(p.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getgid", return_value=1000, create=True)
    def test_live_guest_one_initialize_and_mandatory_disposal(self, *_mocks):
        calls = []
        def fake(argv, timeout=50):
            calls.append(argv)
            if argv[1] == "run":
                return done(argv, out=json.dumps(good()))
            if argv[1] == "rm":
                return done(argv)
            if argv[1:3] == ["container", "exists"]:
                return done(argv, status=1)
            raise AssertionError(argv)
        with (mock.patch.object(p, "verify_inputs", return_value=M0_ID),
              mock.patch.object(p, "inspect_composed", return_value=p.OWNER_IMAGE_ID),
              mock.patch.object(p, "verify_combined", return_value={
                  "registry": {"id": "opencode", "installation": "installed",
                               "expectedCommand": True}}),
              mock.patch.object(p, "fixed", side_effect=fake)):
            outcome = p.probe()
        self.assertEqual(outcome["status"], "ACPX_OPENCODE_INITIALIZE_PASS", outcome)
        self.assertEqual(outcome["protocol"]["protocol_version"], "1")
        self.assertTrue(outcome["guest_removed"])
        self.assertFalse(outcome["actual_acp_session_created"])
        self.assertFalse(outcome["model_inference"])
        self.assertEqual([x[1] for x in calls].count("run"), 1)
        self.assertEqual([x[1] for x in calls].count("rm"), 1)

    @mock.patch.object(p.sys, "platform", "linux")
    @mock.patch.object(p.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(p.os, "getgid", return_value=1000, create=True)
    def test_invalid_protocol_or_failed_cleanup_never_passes(self, *_mocks):
        for state in ("bad", "cleanup"):
            with self.subTest(state=state):
                def fake(argv, timeout=50):
                    if argv[1] == "run":
                        return done(argv, out=json.dumps(
                            {**good(), "ok": False} if state == "bad" else good()))
                    if argv[1] == "rm":
                        return done(argv)
                    if argv[1:3] == ["container", "exists"]:
                        return done(argv, status=0 if state == "cleanup" else 1)
                    raise AssertionError(argv)
                with (mock.patch.object(p, "verify_inputs", return_value=M0_ID),
                      mock.patch.object(p, "inspect_composed", return_value=p.OWNER_IMAGE_ID),
                      mock.patch.object(p, "verify_combined", return_value={
                          "registry": {"id": "opencode", "installation": "installed",
                                       "expectedCommand": True}}),
                      mock.patch.object(p, "fixed", side_effect=fake)):
                    outcome = p.probe()
                self.assertEqual(outcome["status"], "BLOCKED", outcome)

    @mock.patch.object(p.sys, "platform", "linux")
    @mock.patch.object(p.os, "geteuid", return_value=1000, create=True)
    def test_wrong_local_immutable_image_never_runs_handshake(self, *_mocks):
        calls = []
        def fake(argv, timeout=50):
            calls.append(argv)
            if argv[1:3] == ["container", "exists"]:
                return done(argv, status=1)
            return done(argv)
        with (mock.patch.object(p, "verify_inputs", return_value=M0_ID),
              mock.patch.object(p, "inspect_composed", return_value="f" * 64),
              mock.patch.object(p, "fixed", side_effect=fake)):
            result = p.probe()
        self.assertEqual(result["status"], "BLOCKED")
        self.assertFalse(any(x[1] == "run" for x in calls))

    @mock.patch.object(p.sys, "platform", "win32")
    def test_windows_not_qualified(self):
        with mock.patch.object(p, "fixed") as run:
            report = p.probe()
        self.assertEqual(report["status"], "BLOCKED")
        run.assert_not_called()

if __name__ == "__main__":
    unittest.main()
