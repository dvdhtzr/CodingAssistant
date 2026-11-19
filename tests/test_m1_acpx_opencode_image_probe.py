"""M1.7 offline image composition and read-only Gateway discovery contracts.

No real Podman build, Gateway, OpenCode, ACP, or inference runs in CI.
The Dell verification still must independently establish runtime behavior.
"""
from __future__ import annotations
import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import types
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools/m1"))
sys.path.insert(0, str(ROOT / "tools/m0"))
spec = importlib.util.spec_from_file_location(
    "acpx_opencode_image_probe", ROOT / "tools/m1/acpx_opencode_image_probe.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
M0_ID = "a" * 64
CHILD_ID = "f" * 64


def done(argv, status=0, out=""):
    return subprocess.CompletedProcess(argv, status, out, "")


def registry():
    return {"id": "opencode", "installation": "installed",
            "expectedCommand": True}


def fake_image():
    return [{"Id": "sha256:" + CHILD_ID,
             "RepoTags": [m.IMAGE_TAG],
             "Labels": {
                 **m.LABELS,
                 "io.github.dvdhtzr.codingassistant.m0_image_id": M0_ID,
                 "io.github.dvdhtzr.codingassistant.gateway_image_id": m.GATEWAY_IMAGE_ID,
             }}]


class OfflineOpenCodeCompositionTest(unittest.TestCase):
    def test_containerfile_is_copy_only_two_local_inputs(self):
        content = m.CONTAINERFILE.read_text(encoding="utf-8")
        self.assertIn("FROM ${M0_IMAGE} AS qualified_opencode", content)
        self.assertIn("FROM ${GATEWAY_IMAGE}", content)
        self.assertIn("COPY --from=qualified_opencode", content)
        self.assertIn("/usr/local/lib/node_modules/@opencode/cli", content)
        self.assertIn("/usr/local/bin/opencode", content)
        for line in content.splitlines():
            stripped = line.lstrip()
            self.assertFalse(stripped.startswith("RUN "), line)
            self.assertFalse(stripped.startswith("ADD "), line)
        self.assertNotIn("FROM node:latest", content)
        self.assertNotIn("npm install", "\n".join(
            x for x in content.splitlines() if not x.lstrip().startswith("#")))

    def test_build_has_no_network_downloads_or_private_context(self):
        cmd = m.build_command(M0_ID)
        self.assertEqual(cmd[:2], ["podman", "build"])
        for flag in ("--pull=never", "--network=none", "--build-arg"):
            self.assertIn(flag, cmd)
        self.assertIn("M0_IMAGE=" + M0_ID, cmd)
        self.assertIn("GATEWAY_IMAGE=" + m.GATEWAY_IMAGE_ID, cmd)
        self.assertEqual(cmd[-1], str(m.CONTEXT))
        for forbidden in ("--volume", "--mount", "--secret", "--push",
                          "--network=host", "--privileged"):
            self.assertNotIn(forbidden, cmd)
        with self.assertRaises(ValueError):
            m.build_command("latest")

    def test_m0_must_be_qualified_with_immutable_id(self):
        with mock.patch.object(m, "m0_evaluate", return_value={
            "status": "TOOLCHAIN_PASS", "image_id": M0_ID,
            "checks": {"opencode_pinned": True, "node_22": True}
        }):
            self.assertEqual(m.m0_verified_id(), M0_ID)
        for result in (
            {"status": "BLOCKED", "image_id": M0_ID, "checks": {}},
            {"status": "TOOLCHAIN_PASS", "image_id": "mutable-tag",
             "checks": {"opencode_pinned": True}},
            {"status": "TOOLCHAIN_PASS", "image_id": M0_ID,
             "checks": {"opencode_pinned": False}},
        ):
            with mock.patch.object(m, "m0_evaluate", return_value=result):
                with self.assertRaises(ValueError):
                    m.m0_verified_id()

    def test_derived_image_identity_and_input_labels_required(self):
        with mock.patch.object(m, "command",
                               return_value=done([], out=json.dumps(fake_image()))):
            self.assertEqual(m.inspect_composed(M0_ID), CHILD_ID)
        for changed in (
            [dict(fake_image()[0], Labels={})],
            [dict(fake_image()[0], Id="sha256:" + M0_ID)],
            [dict(fake_image()[0], RepoTags=["other:tag"])],
            [dict(fake_image()[0], Id="not-a-digest")],
        ):
            with mock.patch.object(m, "command",
                                   return_value=done([], out=json.dumps(changed))):
                with self.assertRaises(ValueError):
                    m.inspect_composed(M0_ID)

    def test_guest_only_exact_no_network_version_help_registry(self):
        with mock.patch.object(m.os, "getuid", return_value=1000, create=True), \
             mock.patch.object(m.os, "getgid", return_value=1000, create=True):
            base = m.guest(CHILD_ID, ("--version",))
            native = m.binary_guest(CHILD_ID, ("acp", "--help"))
        for command in (base, native):
            for flag in ("--network=none", "--read-only", "--pull=never",
                         "--cap-drop=all", "--user=1000:1000",
                         "--security-opt=no-new-privileges"):
                self.assertIn(flag, command)
            for denied in ("--publish", "--volume", "--mount", "--privileged"):
                self.assertNotIn(denied, command)
        self.assertIn("--entrypoint=node", base)
        self.assertIn("--entrypoint=/usr/local/bin/opencode", native)
        self.assertEqual(native[-2:], ["acp", "--help"])
        with mock.patch.object(m.os, "getuid", return_value=1000, create=True), \
             mock.patch.object(m.os, "getgid", return_value=1000, create=True):
            for args in (("run",), ("acp",), ("acp", "start"),
                         ("-e", "require('child_process').exec('whoami')")):
                with self.subTest(args=args), self.assertRaises(ValueError):
                    m.guest(CHILD_ID, args)
            with self.assertRaises(ValueError):
                m.binary_guest(CHILD_ID, ("acp",))

    def test_offline_binary_cli_and_registry_verification(self):
        def fake(argv, timeout=60, **kw):
            if "--input-type=module" in argv:
                return done(argv, out=json.dumps(registry()))
            if "--entrypoint=/usr/local/bin/opencode" in argv:
                if argv[-2:] == ["acp", "--help"]:
                    return done(argv, out="Usage: opencode acp")
                return done(argv, out="2.3.0")
            if argv[-2:] == ["/app/openclaw.mjs", "--version"]:
                return done(argv, out="2026.11.5")
            if argv[-1] == "--version":
                return done(argv, out="v24.22.1")
            raise AssertionError(argv)
        with mock.patch.object(m, "command", side_effect=fake), \
             mock.patch.object(m.os, "getuid", return_value=1000, create=True), \
             mock.patch.object(m.os, "getgid", return_value=1000, create=True):
            out = m.verify_combined(CHILD_ID)
        self.assertTrue(out["acp_help"])
        self.assertEqual(out["registry"], registry())
        with mock.patch.object(m, "command", side_effect=lambda argv, **kw:
                  done(argv, out="2.3.1" if "--entrypoint=/usr/local/bin/opencode" in argv
                       else fake(argv, **kw).stdout)), \
             mock.patch.object(m.os, "getuid", return_value=1000, create=True), \
             mock.patch.object(m.os, "getgid", return_value=1000, create=True):
            with self.assertRaises(ValueError):
                m.verify_combined(CHILD_ID)

    def test_single_readonly_rpc_requires_installation_and_cleanup(self):
        calls = []
        def mock_podman(argv, timeout=50):
            calls.append(argv)
            op = argv[1]
            if op == "run":
                return done(argv, out="container-id")
            if op == "inspect":
                return done(argv, out="true")
            if op == "exec":
                if "gateway" in argv:
                    self.assertIn("acpx.agents.list", argv)
                    return done(argv, out=json.dumps({"agents": [{
                        "id": "opencode", "runtimeId": "acp-opencode",
                        "installation": "installed", "enabled": True}]}))
                return done(argv, out=json.dumps({
                    "/healthz": 200, "/startupz": 200, "/readyz": 200}))
            if op == "rm":
                return done(argv)
            if op == "container" and argv[2] == "exists":
                return done(argv, status=1)
            raise AssertionError(argv)
        with mock.patch("gateway_image_probe.fixed", side_effect=mock_podman), \
             mock.patch.object(m.os, "getuid", return_value=1000, create=True), \
             mock.patch.object(m.os, "getgid", return_value=1000, create=True):
            report = m.rpc_installed(CHILD_ID)
        self.assertTrue(report["gateway_ready"])
        self.assertTrue(report["removed"])
        self.assertEqual(report["adapter"]["installation"], "installed")
        self.assertEqual(sum(a[1] == "rm" for a in calls), 1)
        self.assertEqual(sum(a[1] == "run" for a in calls), 1)
        self.assertIn("--network=none", next(a for a in calls if a[1] == "run"))
        self.assertFalse(any("--publish" in a or "--volume" in a for a in calls))

    def test_absent_derived_image_does_not_build_implicitly(self):
        with mock.patch.object(m.sys, "platform", "linux"), \
             mock.patch.object(m.os, "geteuid", return_value=1000, create=True), \
             mock.patch.object(m, "verify_inputs", return_value=M0_ID), \
             mock.patch.object(m, "inspect_composed", return_value=None), \
             mock.patch.object(m, "command") as called:
            result = m.evaluate(False)
        self.assertEqual(result["status"], "BLOCKED")
        self.assertIn("--prepare", result["reason"])
        called.assert_not_called()

    def test_explicit_prepare_still_cannot_download_or_run_agent(self):
        called = []
        def fake(argv, timeout=60, log=None):
            called.append(argv)
            self.assertEqual(argv[:2], ["podman", "build"])
            self.assertIn("--network=none", argv)
            self.assertIn("--pull=never", argv)
            self.assertIsNotNone(log)
            return done(argv)
        with mock.patch.object(m.sys, "platform", "linux"), \
             mock.patch.object(m.os, "geteuid", return_value=1000, create=True), \
             mock.patch.object(m, "verify_inputs", return_value=M0_ID), \
             mock.patch.object(m, "command", side_effect=fake), \
             mock.patch.object(m, "inspect_composed", return_value=CHILD_ID), \
             mock.patch.object(m, "verify_combined",
                               return_value={"registry": registry()}), \
             mock.patch.object(m, "rpc_installed",
                               return_value={"removed": True,
                                             "gateway_ready": True}), \
             mock.patch.object(m.shutil, "disk_usage",
                               return_value=types.SimpleNamespace(free=8*1024**3)), \
             tempfile.TemporaryDirectory() as temp, \
             mock.patch.object(m, "ARTIFACTS", pathlib.Path(temp)):
            result = m.evaluate(True)
        self.assertEqual(result["status"], "ACPX_OPENCODE_INSTALLED_DISCOVERY_PASS", result)
        self.assertFalse(result["agent_spawned"])
        self.assertFalse(result["model_inference"])
        self.assertEqual(len(called), 1)

    @mock.patch.object(m.sys, "platform", "win32")
    def test_windows_not_qualified_or_built(self):
        with mock.patch.object(m, "command") as called:
            result = m.evaluate(True)
        self.assertEqual(result["status"], "BLOCKED")
        called.assert_not_called()


if __name__ == "__main__":
    unittest.main()
