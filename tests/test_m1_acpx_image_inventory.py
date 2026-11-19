"""M1.3 static image inventory tests; all Podman interactions mocked."""
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
    "acpx_image_inventory", ROOT / "tools/m1/acpx_image_inventory.py")
m1 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m1)


def output(present=None, *, version=None, dep=None):
    present = present or {
        "manifest": False, "entry": False, "package": False,
        "dependencyAtRoot": False, "dependencyAtPlugin": False,
    }
    return {
        "manifest": "acpx" if present["manifest"] else None,
        "pluginPackageVersion": version if present["package"] else None,
        "acpxDependencyRoot": dep if present["dependencyAtRoot"] else None,
        "acpxDependencyPlugin": dep if present["dependencyAtPlugin"] else None,
        "present": present,
    }


def done(cmd, status=0, text=""):
    return subprocess.CompletedProcess(cmd, status, text, "")


class AcpxInventoryContracts(unittest.TestCase):
    def test_fixed_readonly_guest_never_has_gateway_or_external_network(self):
        cmd = m1.command(m1.APPROVED_IMAGE_ID, 1000, 1000)
        for flag in (
            "--network=none", "--read-only", "--cap-drop=all",
            "--security-opt=no-new-privileges", "--userns=keep-id",
            "--user=1000:1000", "--pull=never", "--entrypoint=node",
            "--pids-limit=64", "--memory=2g",
        ):
            self.assertIn(flag, cmd)
        for forbidden in (
            "--volume", "--mount", "--privileged", "--publish",
            "--network=host", "--device", "gateway", "acp", "install",
        ):
            self.assertNotIn(forbidden, cmd)
        self.assertEqual(cmd[-3], m1.APPROVED_IMAGE_ID)
        self.assertEqual(cmd[-2], "-e")
        self.assertIn("/app/dist/extensions/acpx/openclaw.plugin.json", cmd[-1])
        self.assertIn("/app/dist/extensions/acpx/index.js", cmd[-1])
        with self.assertRaises(ValueError):
            m1.command("f" * 64, 1000, 1000)
        with self.assertRaises(ValueError):
            m1.command(m1.APPROVED_IMAGE_ID, 0, 1000)

    def test_reject_malformed_guest_report(self):
        good = output()
        self.assertEqual(m1.parse_report(json.dumps(good)), good)
        examples = [
            {"unexpected": True},
            dict(good, manifest="acpx"),
            dict(good, present=dict(good["present"], entry=1)),
            dict(good, present=dict(good["present"], extra=True)),
            dict(good, pluginPackageVersion="../secrets"),
            dict(good, pluginPackageVersion="x" * 80),
            "not a mapping",
        ]
        for case in examples:
            with self.subTest(case=case):
                with self.assertRaises(ValueError):
                    m1.parse_report(json.dumps(case))
        with self.assertRaises(ValueError):
            m1.parse_report("x" * 3501)

    @mock.patch.object(m1.sys, "platform", "linux")
    @mock.patch.object(m1.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(m1.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(m1.os, "getgid", return_value=1000, create=True)
    def test_optional_plugin_absence_is_discovery_not_acp_ready(self, *_mocks):
        calls = []
        def fake(cmd, timeout=50):
            calls.append(cmd)
            if cmd[1] == "info":
                return done(cmd, text="true\n")
            if cmd[1] == "run":
                return done(cmd, text=json.dumps(output()))
            raise AssertionError("Only rootless info and offline image run allowed")
        with (mock.patch.object(m1, "fixed", side_effect=fake),
              mock.patch.object(m1, "inspected_image",
                                return_value={"image_id": m1.APPROVED_IMAGE_ID,
                                              "repo_digest": m1.APPROVED_REPO_DIGEST})):
            result = m1.run()
        self.assertEqual(result["status"], "INVENTORY_COMPLETE", result)
        self.assertEqual(result["plugin_distribution"], "NOT_BUNDLED")
        self.assertFalse(result["acp_session_started"])
        self.assertFalse(result["plugins_installed"])
        self.assertEqual(sum(cmd[1] == "run" for cmd in calls), 1)
        self.assertFalse(any("pull" in cmd for cmd in calls))

    @mock.patch.object(m1.sys, "platform", "linux")
    @mock.patch.object(m1.os, "geteuid", return_value=1000, create=True)
    @mock.patch.object(m1.os, "getuid", return_value=1000, create=True)
    @mock.patch.object(m1.os, "getgid", return_value=1000, create=True)
    def test_present_plugin_is_not_claimed_activated(self, *_mocks):
        allfiles = {k: True for k in output()["present"]}
        def fake(cmd, timeout=50):
            if cmd[1] == "info":
                return done(cmd, text="true\n")
            return done(cmd, text=json.dumps(
                output(allfiles, version="2026.11.5", dep="0.21.0")))
        with (mock.patch.object(m1, "fixed", side_effect=fake),
              mock.patch.object(m1, "inspected_image",
                                return_value={"image_id": m1.APPROVED_IMAGE_ID,
                                              "repo_digest": m1.APPROVED_REPO_DIGEST})):
            result = m1.run()
        self.assertEqual(result["status"], "INVENTORY_COMPLETE")
        self.assertEqual(result["plugin_distribution"],
                         "FILES_PRESENT_NOT_ACTIVATED")
        self.assertFalse(result["acp_session_started"])

    @mock.patch.object(m1.sys, "platform", "linux")
    @mock.patch.object(m1.os, "geteuid", return_value=1000, create=True)
    def test_image_mismatch_refuses_guest_run(self, *_mocks):
        seen = []
        def fake(cmd, timeout=50):
            seen.append(cmd)
            return done(cmd, text="true\n")
        with (mock.patch.object(m1, "fixed", side_effect=fake),
              mock.patch.object(m1, "inspected_image",
                                return_value={"image_id": "a" * 64,
                                              "repo_digest": m1.APPROVED_REPO_DIGEST})):
            result = m1.run()
        self.assertEqual(result["status"], "BLOCKED")
        self.assertFalse(any(cmd[1] == "run" for cmd in seen))

    @mock.patch.object(m1.sys, "platform", "win32")
    def test_no_windows_guest_execution_or_claim(self):
        with mock.patch.object(m1, "fixed") as proc:
            result = m1.run()
        self.assertEqual(result["status"], "BLOCKED")
        proc.assert_not_called()


if __name__ == "__main__":
    unittest.main()
