"""M1.0 safe read-only discovery tests (no actual OpenClaw/Gateway)."""
from __future__ import annotations

import importlib.util
import pathlib
import subprocess
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "m1_discovery", ROOT / "tools/m1/discovery.py")
m1 = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(m1)


class M1PrerequisitesTests(unittest.TestCase):
    def test_node_support_minimum_and_security_floor(self):
        for version, supported, patched in (
            ("v22.22.1", False, False),
            ("v24.15.9", False, False),
            ("v24.16.0", True, False),
            ("v24.22.0", True, False),
            ("v24.22.1", True, True),
            ("v24.30.0", True, True),
            ("v25.0.0", False, False),
            ("v26.0.0", False, False),
            ("v26.1.0", True, False),
            ("v26.12.1", True, True),
            ("v26.13.0", True, True),
            ("v27.0.0", False, False),
            ("unknown", False, False),
            (None, False, False),
        ):
            with self.subTest(version=version):
                found = m1.node_compat(version)
                self.assertEqual(found["supported"], supported)
                self.assertEqual(found["patched_recommended"], patched)

    def test_missing_executable_never_runs_command(self):
        with mock.patch.object(m1.shutil, "which", return_value=None), \
             mock.patch.object(m1.subprocess, "run") as called:
            found = m1.local_version("openclaw")
        self.assertFalse(found["installed"])
        called.assert_not_called()

    def test_only_single_version_command_without_shell_or_credentials(self):
        result = subprocess.CompletedProcess(
            ["node", "--version"], 0, "v24.22.1\n", "")
        with mock.patch.object(m1.shutil, "which", return_value="/tmp/safe-node"), \
             mock.patch.dict(m1.os.environ,
                             {"OPENAI_API_KEY": "SECRET_VALUE",
                              "ANTHROPIC_API_KEY": "ALSO_SECRET",
                              "PATH": "/tmp"}, clear=True), \
             mock.patch.object(m1.subprocess, "run", return_value=result) as call:
            info = m1.local_version("node")
        self.assertEqual(info, {
            "installed": True, "version": "v24.22.1", "exit_code": 0})
        args, kwargs = call.call_args
        self.assertEqual(args[0], ["/tmp/safe-node", "--version"])
        self.assertNotIn("OPENAI_API_KEY", kwargs["env"])
        self.assertNotIn("ANTHROPIC_API_KEY", kwargs["env"])
        self.assertNotIn("shell", kwargs)
        self.assertEqual(kwargs["timeout"], 8)

    def test_version_error_does_not_export_stderr(self):
        result = subprocess.CompletedProcess(["openclaw", "--version"], 1,
                                             "", "secret-path /home/user/.openclaw")
        with mock.patch.object(m1.shutil, "which", return_value="/usr/bin/openclaw"), \
             mock.patch.object(m1.subprocess, "run", return_value=result):
            info = m1.local_version("openclaw")
        self.assertEqual(info["version"], None)
        self.assertNotIn("secret", repr(info))

    def test_prerequisites_do_not_claim_agent_ready(self):
        installed = {
            "node": {"installed": True, "version": "v24.22.1", "exit_code": 0},
            "openclaw": {"installed": True, "version": "2026.11.1", "exit_code": 0},
            "podman": {"installed": True, "version": "5.0.0", "exit_code": 0},
            "opencode": {"installed": False, "version": None, "exit_code": None},
        }
        with mock.patch.object(m1, "local_version",
                               side_effect=lambda name: installed[name]):
            report = m1.discover()
        self.assertEqual(report["status"], "DISCOVERY_READY")
        self.assertEqual(report["acpx_runtime"], "EMBEDDED_PLUGIN_NOT_INSPECTED")
        self.assertEqual(report["gateway_connection"], "NOT_ATTEMPTED")
        self.assertEqual(report["acp_session"], "NOT_ATTEMPTED")
        self.assertEqual(report["model_inference"], "NOT_ATTEMPTED")
        installed["node"] = {"installed": True,
                             "version": "v22.22.1", "exit_code": 0}
        with mock.patch.object(m1, "local_version",
                               side_effect=lambda name: installed[name]):
            report = m1.discover()
        self.assertEqual(report["status"], "PREREQUISITES_BLOCKED")

    def test_probe_rejects_slow_or_broken_installed_binary(self):
        with mock.patch.object(m1.shutil, "which", return_value="/usr/bin/node"), \
             mock.patch.object(m1.subprocess, "run",
                               side_effect=subprocess.TimeoutExpired(
                                   ["node", "--version"], 8)):
            self.assertEqual(m1.local_version("node")["version"], None)


if __name__ == "__main__":
    unittest.main()
