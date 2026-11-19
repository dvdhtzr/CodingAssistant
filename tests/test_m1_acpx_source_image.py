"""M1.4 source-built ACPX image contracts; NO network/Podman/build executed."""
from __future__ import annotations

import importlib.util
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import types
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools/m1"))
spec = importlib.util.spec_from_file_location(
    "acpx_source_image", ROOT / "tools/m1/acpx_source_image.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
ID = "e" * 64


def done(argv, code=0, output=""):
    return subprocess.CompletedProcess(argv, code, output, "")


def inventory(*, with_plugin=True, dep="0.21.0"):
    paths = {
        "manifest": with_plugin, "entry": with_plugin, "package": with_plugin,
        "dependencyAtRoot": False, "dependencyAtPlugin": with_plugin,
    }
    return {
        "present": paths,
        "manifest": "acpx" if with_plugin else None,
        "pluginPackageVersion": "2026.11.5" if with_plugin else None,
        "acpxDependencyRoot": None,
        "acpxDependencyPlugin": dep if with_plugin else None,
    }


class PinnedAcpxBuildContracts(unittest.TestCase):
    def test_public_pinned_source_and_build_without_host_mounts(self):
        with tempfile.TemporaryDirectory() as folder:
            src = pathlib.Path(folder) / "openclaw"
            clone = m.clone_command(src)
            self.assertIn("https://github.com/openclaw/openclaw.git", clone)
            self.assertIn("v2026.11.5", clone)
            self.assertIn("--depth=1", clone)
            self.assertIn("--no-recurse-submodules", clone)
            self.assertIn("credential.helper=", clone)
            build = m.build_command(src)
            self.assertEqual(build[:2], ["podman", "build"])
            self.assertIn("OPENCLAW_EXTENSIONS=acpx", build)
            self.assertIn("OPENCLAW_DOCKER_BUILD_NODE_OPTIONS=--max-old-space-size=6144", build)
            self.assertIn(m.IMAGE_TAG, build)
            self.assertIn(str(src / "Dockerfile"), build)
            self.assertEqual(build[-1], str(src))
            for key, val in m.VERSIONED_LABELS.items():
                self.assertIn(key + "=" + val, build)
            for forbidden in ("--privileged", "--volume", "--mount",
                              "--network=host", "--secret", "--push"):
                self.assertNotIn(forbidden, build)
            self.assertEqual(m.SOURCE_COMMIT,
                             "e13cf15ece9bbd969b7362927c315362c90c97ff")

    def test_never_passes_API_keys_or_GitHub_token_to_process(self):
        with mock.patch.dict(os.environ, {
            "PATH": "/bin", "HOME": "/tmp", "OPENAI_API_KEY": "key",
            "GITHUB_TOKEN": "secret", "ANTHROPIC_API_KEY": "secret",
            "OLLAMA_HOST": "http://wrong-host", "AWS_ACCESS_KEY_ID": "secret",
        }, clear=True):
            base = m.sanitized_env()
            git = m.sanitized_env(git=True)
        for data in (base, git):
            for prohibited in ("OPENAI_API_KEY", "ANTHROPIC_API_KEY",
                               "GITHUB_TOKEN", "OLLAMA_HOST",
                               "AWS_ACCESS_KEY_ID"):
                self.assertNotIn(prohibited, data)
        self.assertEqual(git["GIT_TERMINAL_PROMPT"], "0")
        self.assertEqual(git["GIT_CONFIG_NOSYSTEM"], "1")

    def test_image_metadata_is_frozen_and_offline_probe_only(self):
        metadata = [{
            "Id": "sha256:" + ID,
            "Labels": dict(m.VERSIONED_LABELS),
            "RepoTags": [m.IMAGE_TAG],
        }]
        with mock.patch.object(m, "command",
                               return_value=done([], output=json.dumps(metadata))):
            self.assertEqual(m.inspect_derived(), ID)
        for bad in (
            [dict(metadata[0], Labels={})],
            [dict(metadata[0], Id="sha256:" + m.APPROVED_IMAGE_ID)],
            [dict(metadata[0], RepoTags=["localhost/other:test"])],
            [dict(metadata[0], Id="latest")],
        ):
            with mock.patch.object(m, "command",
                                   return_value=done([], output=json.dumps(bad))):
                with self.assertRaises(ValueError):
                    m.inspect_derived()
        with mock.patch.object(m.os, "getuid", return_value=1000, create=True), \
             mock.patch.object(m.os, "getgid", return_value=1000, create=True):
            for args in (("--version",),
                         ("/app/openclaw.mjs", "--version"),
                         ("-e", m.INVENTORY_JS)):
                cmd = m.offline_guest_command(ID, args)
                self.assertIn("--network=none", cmd)
                self.assertIn("--read-only", cmd)
                self.assertIn("--cap-drop=all", cmd)
                self.assertIn("--pull=never", cmd)
                self.assertIn("--security-opt=no-new-privileges", cmd)
                self.assertEqual(cmd[-len(args):], list(args))
                self.assertNotIn("--volume", cmd)
                self.assertNotIn("--publish", cmd)
            with self.assertRaises(ValueError):
                m.offline_guest_command(ID, ("-e", "require('child_process')"))
            with self.assertRaises(ValueError):
                m.offline_guest_command(ID, ("gateway",))

    def test_plugin_version_is_not_confused_with_acp_readiness(self):
        def fake(cmd, *, timeout=60, log=None, git=False):
            if cmd[-1] == "--version" and cmd[-2] == ID:
                return done(cmd, output="v24.22.1\n")
            if cmd[-1] == "--version" and cmd[-2] == "/app/openclaw.mjs":
                return done(cmd, output="2026.11.5\n")
            if cmd[-2] == "-e":
                return done(cmd, output=json.dumps(inventory()))
            raise AssertionError(cmd)
        with mock.patch.object(m.os, "getuid", return_value=1000, create=True), \
             mock.patch.object(m.os, "getgid", return_value=1000, create=True), \
             mock.patch.object(m, "command", side_effect=fake):
            result = m.verify_offline(ID)
        self.assertEqual(result["node_version"], "v24.22.1")
        self.assertEqual(result["gateway_cli_version"], "2026.11.5")
        self.assertEqual(result["acpx_metadata"]["manifest"], "acpx")
        self.assertNotIn("gateway_started", result)
        with mock.patch.object(m.os, "getuid", return_value=1000, create=True), \
             mock.patch.object(m.os, "getgid", return_value=1000, create=True), \
             mock.patch.object(m, "command", side_effect=lambda cmd, **kw:
                 done(cmd, output=(
                     "v24.22.1" if cmd[-2] == ID else
                     "2026.11.5" if cmd[-2] == "/app/openclaw.mjs" else
                     json.dumps(inventory(with_plugin=False))))):
            with self.assertRaises(ValueError):
                m.verify_offline(ID)

    @mock.patch.object(m, "verify_prerequisites")
    @mock.patch.object(m, "inspect_derived", return_value=None)
    def test_default_absent_build_never_downloads_or_builds(self, image, pre):
        with mock.patch.object(m, "command") as exec_call:
            result = m.build_and_check(prepare=False)
        self.assertEqual(result["status"], "BLOCKED")
        self.assertIn("--prepare", result["reason"])
        exec_call.assert_not_called()
        self.assertFalse(result["prepare_requested"])

    def test_build_is_opt_in_and_marks_only_packaging(self):
        with tempfile.TemporaryDirectory() as root:
            storage = pathlib.Path(root) / "artifacts"
            def fake_command(argv, *, timeout=60, log=None, git=False):
                if argv[0] == "git" and "clone" in argv:
                    src = pathlib.Path(argv[-1])
                    (src / "extensions/acpx").mkdir(parents=True)
                    for filename in ("Dockerfile", "pnpm-lock.yaml"):
                        (src / filename).write_text("fixture", encoding="utf-8")
                    (src / "extensions/acpx/package.json").write_text(json.dumps({
                        "name": "@openclaw/acpx", "version": "2026.11.5",
                        "dependencies": {"acpx": "0.21.0"}
                    }), encoding="utf-8")
                    return done(argv)
                if argv[0] == "git" and "rev-parse" in argv:
                    return done(argv, output=m.SOURCE_COMMIT)
                if argv[0] == "git" and "status" in argv:
                    return done(argv)
                if argv[:2] == ["podman", "build"]:
                    self.assertIn("OPENCLAW_EXTENSIONS=acpx", argv)
                    self.assertIsNotNone(log)
                    return done(argv)
                raise AssertionError(argv)
            with mock.patch.object(m, "ARTIFACTS", storage), \
                 mock.patch.object(m, "verify_prerequisites"), \
                 mock.patch.object(m.shutil, "disk_usage", return_value=
                                   types.SimpleNamespace(free=40 * 1024**3)), \
                 mock.patch.object(m, "command", side_effect=fake_command), \
                 mock.patch.object(m, "inspect_derived", return_value=ID), \
                 mock.patch.object(m, "verify_offline",
                                   return_value={"node_version": "v24.22.1",
                                                 "acpx_metadata": inventory()}):
                result = m.build_and_check(prepare=True)
            self.assertEqual(result["status"], "ACPX_IMAGE_PACKAGED_PASS", result)
            self.assertTrue(result["plugin_packaged"])
            self.assertTrue(result["source_verified"])
            self.assertFalse(result["gateway_started"])
            self.assertFalse(result["acp_session_started"])

    @mock.patch.object(m.sys, "platform", "win32")
    def test_windows_refuses_build(self):
        with mock.patch.object(m, "command") as exec_call:
            result = m.build_and_check(prepare=True)
        self.assertEqual(result["status"], "BLOCKED")
        exec_call.assert_not_called()


if __name__ == "__main__":
    unittest.main()
