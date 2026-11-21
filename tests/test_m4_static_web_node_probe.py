"""Model-free M4.1 contract tests: never launch a real Podman guest."""
from __future__ import annotations

import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "static_web_node_probe", ROOT / "tools/m4/static_web_node_probe.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)

M0 = "a" * 64
REGISTRY = {"registry": {"id": "opencode", "installation": "installed",
                         "expectedCommand": True}}


def completed(cmd, code=0, output=""):
    return subprocess.CompletedProcess(cmd, code, output, "")


class Phase4NodeSyntaxBoundary(unittest.TestCase):
    def test_trusted_payload_contains_syntax_only_and_distinct_attacks(self):
        self.assertEqual(len(m.CASES), 13)
        self.assertEqual(len(set(m.CASES)), 13)
        text = m.WORKLOAD_JS
        for expected in (
            "['--check', '-']", "function broken( {",
            "syntax_check_did_not_execute",
            "/tmp/ca-m4-syntax-never-executed",
            "symlinkSync('/etc/passwd'",
            "path_traversal_write_denied",
            "readFileSync('/etc/shadow')",
            "process.setuid(0)", "OPENAI_API_KEY", "GITHUB_TOKEN",
            "'203.0.113.1', 443",
            "child_external_network_denied",
            "child_system_write_denied",
            "git_remote_escape_denied", "'ls-remote'",
        ):
            self.assertIn(expected, text)
        for forbidden in (
            "session/new", "session/prompt", "git push", "http://127.0.0.1",
            "GITHUB_TOKEN=", "CODINGASSISTANT_OWNER_TOKEN",
        ):
            self.assertNotIn(forbidden, text)
        # Node's syntax checker only parses the fixed payload, never runs it.
        parsed = subprocess.run(
            ["node", "--check", "-"], input=text, text=True,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=10,
            check=False)
        self.assertEqual(parsed.returncode, 0, parsed.stderr)

    def test_pinned_guest_has_no_host_mount_and_never_elevates(self):
        cmd = m.command_for(m.OWNER_IMAGE_ID, "ca-m4-node-012345abcdef",
                            1000, 1000)
        for required in (
            "--rm", "--pull=never", "--network=none", "--read-only",
            "--cap-drop=all", "--security-opt=no-new-privileges",
            "--userns=keep-id", "--user=1000:1000", "--pids-limit=64",
            "--memory=1g", "--cpus=1",
            "--tmpfs=/tmp:rw,nosuid,nodev,size=128m,notmpcopyup",
            "--entrypoint=node",
        ):
            self.assertIn(required, cmd)
        for forbidden in (
            "--volume", "--mount", "--publish", "--privileged",
            "--env-file", "--device", "--network=host", "--secret",
        ):
            self.assertNotIn(forbidden, cmd)
        self.assertEqual(cmd[cmd.index("--entrypoint=node")+1],
                         m.OWNER_IMAGE_ID)
        self.assertEqual(cmd[-2], "-e")
        self.assertEqual(cmd[-1], m.WORKLOAD_JS)
        for image, name, uid, gid in (
            ("f" * 64, "ca-m4-node-012345abcdef", 1000, 1000),
            (m.OWNER_IMAGE_ID, "ca-m4-node-bad", 1000, 1000),
            (m.OWNER_IMAGE_ID, "ca-m4-node-012345abcdef", 0, 1000),
            (m.OWNER_IMAGE_ID, "ca-m4-node-012345abcdef", 1000, 0),
        ):
            with self.subTest(image=image, name=name, uid=uid, gid=gid):
                with self.assertRaises(ValueError):
                    m.command_for(image, name, uid, gid)

    def test_results_are_exact_bounded_booleans(self):
        positive = {case: True for case in m.CASES}
        self.assertEqual(m.parse_observations(json.dumps(positive)), positive)
        for invalid in (
            {**positive, m.CASES[2]: False},
            {**positive, m.CASES[0]: "true"},
            {**positive, "fake_verified": True},
            {k: v for k, v in positive.items() if k != m.CASES[3]},
            [],
            "null",
            {},
        ):
            with self.subTest(invalid=invalid):
                with self.assertRaises(ValueError):
                    m.parse_observations(json.dumps(invalid))
        with self.assertRaises(ValueError):
            m.parse_observations(" " * 3000)

    @mock.patch.object(m.sys, "platform", "linux")
    @mock.patch.object(m.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(m.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(m.os, "getgid", return_value=1000, create=True)
    def test_clean_fixture_pass_cannot_qualify_runtime(self, *_):
        cmds = []
        def fake(cmd, timeout=50):
            cmds.append(cmd)
            if cmd[1] == "run":
                return completed(cmd, output=json.dumps(
                    {case: True for case in m.CASES}))
            if cmd[1] == "rm":
                return completed(cmd)
            if cmd[1:3] == ["container", "exists"]:
                return completed(cmd, code=1)
            raise AssertionError(cmd)
        with (
            mock.patch.object(m, "verify_inputs", return_value=M0),
            mock.patch.object(m, "inspect_composed", return_value=m.OWNER_IMAGE_ID),
            mock.patch.object(m, "verify_combined", return_value=REGISTRY),
            mock.patch.object(m, "fixed", side_effect=fake),
        ):
            report = m.run_probe()
        self.assertEqual(report["status"], "NODE_SYNTAX_FIXTURE_PASS", report)
        self.assertEqual(report["passed"], 13)
        self.assertTrue(report["guest_removed"])
        self.assertTrue(report["provenance_verified"])
        for key in ("model_inference", "user_source_used", "source_writes",
                    "browser_executed", "runtime_profile_qualified",
                    "production_execution_enabled"):
            self.assertFalse(report[key])
        self.assertEqual(sum(c[1] == "run" for c in cmds), 1)
        self.assertEqual(sum(c[1] == "rm" for c in cmds), 1)
        self.assertEqual(sum(c[1:3] == ["container", "exists"] for c in cmds), 1)

    @mock.patch.object(m.sys, "platform", "linux")
    @mock.patch.object(m.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(m.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(m.os, "getgid", return_value=1000, create=True)
    def test_adversarial_failures_and_cleanup_fail_closed(self, *_):
        for scenario in ("attack", "missing_case", "wrong_type", "guest",
                         "cleanup", "image", "registry"):
            calls = []
            def fake(cmd, timeout=50):
                calls.append(cmd)
                if cmd[1] == "run":
                    outcome = {case: True for case in m.CASES}
                    if scenario == "attack":
                        outcome["child_external_network_denied"] = False
                    if scenario == "missing_case":
                        del outcome["syntax_check_did_not_execute"]
                    if scenario == "wrong_type":
                        outcome["system_credentials_read_denied"] = "true"
                    return completed(cmd, code=1 if scenario == "guest" else 0,
                                     output=json.dumps(outcome))
                if cmd[1] == "rm":
                    return completed(cmd)
                if cmd[1:3] == ["container", "exists"]:
                    return completed(cmd, code=0 if scenario == "cleanup" else 1)
                raise AssertionError(cmd)
            with self.subTest(scenario=scenario), (
                mock.patch.object(m, "verify_inputs", return_value=M0),
                mock.patch.object(m, "inspect_composed",
                                  return_value="f" * 64 if scenario == "image"
                                  else m.OWNER_IMAGE_ID),
                mock.patch.object(m, "verify_combined",
                                  return_value={} if scenario == "registry"
                                  else REGISTRY),
                mock.patch.object(m, "fixed", side_effect=fake),
            ):
                report = m.run_probe()
                self.assertEqual(report["status"], "BLOCKED", report)
                self.assertFalse(report["runtime_profile_qualified"])
                if scenario in ("image", "registry"):
                    self.assertFalse(any(c[1] == "run" for c in calls))
                if scenario == "cleanup":
                    self.assertFalse(report["guest_removed"])

    @mock.patch.object(m.sys, "platform", "win32")
    def test_windows_does_not_run_linux_guest(self):
        with mock.patch.object(m, "fixed") as runner:
            report = m.run_probe()
        self.assertEqual(report["status"], "BLOCKED")
        runner.assert_not_called()

    def test_explicit_opt_in_and_private_evidence(self):
        with mock.patch.object(m, "run_probe") as run:
            self.assertEqual(m.main([]), 0)
            run.assert_not_called()
        with tempfile.TemporaryDirectory() as root:
            with mock.patch.object(m, "ARTIFACTS", pathlib.Path(root)):
                path = m.write_evidence({
                    "status": "BLOCKED", "runtime_profile_qualified": False
                })
                self.assertEqual(path.parent, pathlib.Path(root))
                self.assertTrue(path.name.startswith("node-syntax-qualification-"))
                saved = json.loads(path.read_text(encoding="utf-8"))
                self.assertFalse(saved["runtime_profile_qualified"])
                if os.name != "nt":
                    self.assertEqual(path.stat().st_mode & 0o777, 0o600)


if __name__ == "__main__":
    unittest.main()
