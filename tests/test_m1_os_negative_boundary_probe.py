"""M1.9 adversarial guest admission/negative security tests (all Podman mocked)."""
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
    "os_negative_boundary_probe", ROOT / "tools/m1/os_negative_boundary_probe.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
M0 = "a" * 64

def done(cmd, status=0, out=""):
    return subprocess.CompletedProcess(cmd, status, out, "")

class NegativeOsBoundaryTests(unittest.TestCase):
    def test_twelve_real_distinct_escape_attempts_encoded(self):
        self.assertEqual(len(m.CASES), 12)
        self.assertEqual(len(set(m.CASES)), 12)
        js = m.NEGATIVE_JS
        for phrase in (
            "writeFileSync('/etc/ca-negative-boundary-probe'",
            "fs.symlinkSync('/etc/passwd',link)",
            "writeFileSync('/tmp/../etc/",
            "fs.readFileSync('/etc/shadow')",
            "fs.readFileSync('/proc/1/root/etc/shadow')",
            "'/home/dave/CodingAssistant'",
            "OPENAI_API_KEY", "GITHUB_TOKEN", "OLLAMA_HOST",
            "process.setuid(0)",
            "'203.0.113.1',443", "'127.0.0.1',11434",
            "cp.spawnSync(process.execPath", "child_process_outbound_denied",
            "child_process_system_write_denied",
        ):
            self.assertIn(phrase, js)
        self.assertNotIn("github.com", js)
        self.assertNotIn("session/new", js)
        self.assertNotIn("session/prompt", js)

    def test_only_offline_nonroot_no_mount_image(self):
        cmd = m.command_for(
            m.OWNER_IMAGE_ID, "ca-m19-negative-012345abcdef", 1000, 1000)
        for required in (
            "--pull=never", "--network=none", "--read-only", "--cap-drop=all",
            "--security-opt=no-new-privileges", "--userns=keep-id",
            "--user=1000:1000", "--pids-limit=96", "--memory=2g", "--cpus=2",
            "--entrypoint=node", "--env=HOME=/tmp",
        ):
            self.assertIn(required, cmd)
        for forbidden in (
            "--privileged", "--volume", "--mount", "--publish",
            "--network=host", "--device", "--env-file", "--secret",
        ):
            self.assertNotIn(forbidden, cmd)
        self.assertEqual(cmd[cmd.index("--entrypoint=node")+1], m.OWNER_IMAGE_ID)
        self.assertEqual(cmd[-2], "-e")
        for image, name, uid, gid in (
            ("f"*64, "ca-m19-negative-012345abcdef", 1000, 1000),
            (m.OWNER_IMAGE_ID, "ca-m19-negative-bad", 1000, 1000),
            (m.OWNER_IMAGE_ID, "ca-m19-negative-012345abcdef", 0, 1000),
        ):
            with self.subTest(image=image,name=name), self.assertRaises(ValueError):
                m.command_for(image, name, uid, gid)

    def test_strict_exact_twelve_boolean_evidence(self):
        good = {key: True for key in m.CASES}
        self.assertEqual(m.parse_negative(json.dumps(good)), good)
        for bad in (
            {**good, m.CASES[0]: False},
            {**good, m.CASES[0]: "true"},
            {**good, "unexpected": True},
            {k: v for k, v in good.items() if k != m.CASES[-1]},
            [],
            "not-a-dict",
        ):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                m.parse_negative(json.dumps(bad))
        with self.assertRaises(ValueError):
            m.parse_negative(" " * 2100)

    @mock.patch.object(m.sys, "platform", "linux")
    @mock.patch.object(m.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(m.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(m.os, "getgid", return_value=1000, create=True)
    def test_twelve_denials_and_verified_cleanup_required(self, *_mocks):
        calls = []
        def fake(cmd, timeout=50):
            calls.append(cmd)
            if cmd[1] == "run":
                return done(cmd, out=json.dumps({k: True for k in m.CASES}))
            if cmd[1] == "rm":
                return done(cmd)
            if cmd[1:3] == ["container", "exists"]:
                return done(cmd, status=1)
            raise AssertionError(cmd)
        with (mock.patch.object(m, "verify_inputs", return_value=M0),
              mock.patch.object(m, "inspect_composed", return_value=m.OWNER_IMAGE_ID),
              mock.patch.object(m, "verify_combined", return_value={
                  "registry": {"id":"opencode","installation":"installed",
                               "expectedCommand":True}}),
              mock.patch.object(m, "fixed", side_effect=fake)):
            report = m.run()
        self.assertEqual(report["status"], "NEGATIVE_SECURITY_PASS", report)
        self.assertEqual(report["passed_cases"], 12)
        self.assertTrue(report["disposable_guest_removed"])
        self.assertFalse(report["agent_spawned"])
        self.assertFalse(report["model_inference"])
        self.assertEqual([c[1] for c in calls].count("run"), 1)
        self.assertEqual([c[1] for c in calls].count("rm"), 1)

    @mock.patch.object(m.sys, "platform", "linux")
    @mock.patch.object(m.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(m.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(m.os, "getgid", return_value=1000, create=True)
    def test_any_escape_or_cleanup_failure_blocks(self, *_mocks):
        for failure in ("case", "cleanup", "guest"):
            with self.subTest(failure=failure):
                calls = []
                def fake(cmd, timeout=50):
                    calls.append(cmd)
                    if cmd[1] == "run":
                        result = {k: True for k in m.CASES}
                        if failure == "case":
                            result["child_process_outbound_denied"] = False
                        return done(cmd, status=1 if failure=="guest" else 0,
                                    out=json.dumps(result))
                    if cmd[1] == "rm":
                        return done(cmd)
                    if cmd[1:3] == ["container", "exists"]:
                        return done(cmd, status=0 if failure=="cleanup" else 1)
                    raise AssertionError(cmd)
                with (mock.patch.object(m, "verify_inputs", return_value=M0),
                      mock.patch.object(m, "inspect_composed",
                                        return_value=m.OWNER_IMAGE_ID),
                      mock.patch.object(m, "verify_combined", return_value={
                          "registry": {"id":"opencode","installation":"installed",
                                       "expectedCommand":True}}),
                      mock.patch.object(m, "fixed", side_effect=fake)):
                    report = m.run()
                self.assertEqual(report["status"], "BLOCKED", report)
                self.assertEqual([c[1] for c in calls].count("rm"), 1)

    @mock.patch.object(m.sys, "platform", "linux")
    @mock.patch.object(m.os, "geteuid", return_value=1000, create=True)
    def test_wrong_immutable_image_never_runs_guest(self, *_mocks):
        calls = []
        def fake(cmd, timeout=50):
            calls.append(cmd)
            if cmd[1:3] == ["container", "exists"]:
                return done(cmd, status=1)
            return done(cmd)
        with (mock.patch.object(m, "verify_inputs", return_value=M0),
              mock.patch.object(m, "inspect_composed", return_value="f"*64),
              mock.patch.object(m, "fixed", side_effect=fake)):
            report = m.run()
        self.assertEqual(report["status"], "BLOCKED")
        self.assertFalse(any(cmd[1]=="run" for cmd in calls))

    @mock.patch.object(m.sys, "platform", "win32")
    def test_windows_not_qualified_or_executed(self):
        with mock.patch.object(m, "fixed") as fn:
            report = m.run()
        self.assertEqual(report["status"], "BLOCKED")
        fn.assert_not_called()

if __name__ == "__main__":
    unittest.main()
