"""Model-free, cross-platform launcher tests. Never download, start .NET or invoke LLMs."""
import contextlib
import hashlib
import json
import importlib.util
import io
import os
import pathlib
import tempfile
import unittest
import zipfile
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("m3_launcher", ROOT / "tools/m3/launch.py")
launcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(launcher)


class M3LauncherContracts(unittest.TestCase):
    def test_valid_build_requires_real_ui_and_binary(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            app = root / "CodingAssistant.Api"
            app.write_bytes(b"binary")
            (root / "wwwroot").mkdir()
            (root / "wwwroot/index.html").write_text("<title>Mission Control</title>")
            self.assertTrue(launcher.valid_app(root))
            (root / "wwwroot/index.html").write_text("<title>Wrong build</title>")
            self.assertFalse(launcher.valid_app(root))

    def test_safe_artifact_extracts_only_regular_files(self):
        with tempfile.TemporaryDirectory() as temp:
            base = pathlib.Path(temp)
            archive = base / "app.zip"
            dest = base / "payload"
            dest.mkdir()
            with zipfile.ZipFile(archive, "w") as file:
                file.writestr("CodingAssistant.Api", b"binary")
                file.writestr("wwwroot/index.html", "<title>Mission Control</title>")
            launcher.unpack_zip(archive, dest)
            self.assertTrue(launcher.valid_app(dest))

    def test_zip_slip_rejected_without_extracting_outside(self):
        with tempfile.TemporaryDirectory() as temp:
            base = pathlib.Path(temp)
            archive = base / "bad.zip"
            dest = base / "payload"
            dest.mkdir()
            with zipfile.ZipFile(archive, "w") as file:
                file.writestr("../exposed.txt", "no")
            with self.assertRaises(launcher.LaunchError):
                launcher.unpack_zip(archive, dest)
            self.assertFalse((base / "exposed.txt").exists())

    def test_symlink_zip_member_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            base = pathlib.Path(temp)
            archive = base / "link.zip"
            dest = base / "payload"
            dest.mkdir()
            with zipfile.ZipFile(archive, "w") as file:
                member = zipfile.ZipInfo("wwwroot/index.html")
                member.create_system = 3
                member.external_attr = (0o120777 << 16)
                file.writestr(member, "../../home/owner/.ssh")
            with self.assertRaises(launcher.LaunchError):
                launcher.unpack_zip(archive, dest)

    def test_no_gh_no_zip_does_not_start_runtime(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            with mock.patch.object(launcher.Path, "home", return_value=root), \
                 mock.patch.object(launcher, "public_release", return_value=None), \
                 mock.patch.object(launcher, "gh_download", return_value=False), \
                 mock.patch.object(launcher.subprocess, "Popen",
                                   side_effect=AssertionError("API unexpectedly started")), \
                 contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaises(launcher.LaunchError):
                    launcher.install_app(root / "install", interactive=False)

    def test_normal_launch_is_disabled_by_default_even_with_ambient_opt_in(self):
        fake = mock.Mock()
        fake.poll.return_value = None
        fake.wait.return_value = 0
        response = mock.MagicMock()
        response.__enter__.return_value.status = 200
        opener = mock.Mock()
        opener.open.return_value = response
        with mock.patch.dict(os.environ, {
            "CODINGASSISTANT_ENABLE_LOCAL_EXECUTION": "1",
            "CODINGASSISTANT_REPOSITORY_ROOT": "/unapproved",
        }), mock.patch.object(launcher, "free_port", return_value=True), \
             mock.patch.object(launcher.secrets, "token_hex", return_value="a" * 64), \
             mock.patch.object(launcher.subprocess, "Popen", return_value=fake) as popen, \
             mock.patch.object(launcher.urllib.request, "build_opener", return_value=opener), \
             mock.patch.object(launcher.webbrowser, "open") as browser, \
             contextlib.redirect_stdout(io.StringIO()):
            launcher.start(pathlib.Path("/fixture/CodingAssistant.Api"))
        env = popen.call_args.kwargs["env"]
        self.assertEqual(env["CODINGASSISTANT_ENABLE_LOCAL_EXECUTION"], "0")
        self.assertNotIn("CODINGASSISTANT_REPOSITORY_ROOT", env)
        self.assertEqual(env["CODINGASSISTANT_OWNER_TOKEN"], "a" * 64)
        browser.assert_called_once_with("http://127.0.0.1:5098/")

    def test_execution_preflight_uses_model_free_checks_only(self):
        fake = mock.Mock(returncode=0)
        with mock.patch.object(launcher.subprocess, "run", return_value=fake) as run, \
             contextlib.redirect_stdout(io.StringIO()):
            launcher.verify_execution_preflight()
        args = run.call_args.args[0]
        self.assertEqual(args[-2:], [str(launcher.ROOT / "tools/m2/dell_acceptance.py"),
                                      "--preflight-only"])
        self.assertNotIn("--run", args)
        self.assertEqual(run.call_args.kwargs["cwd"], launcher.ROOT)
        self.assertEqual(run.call_args.kwargs["stdin"], launcher.subprocess.DEVNULL)

    def test_failed_execution_preflight_denies_launch(self):
        fake = mock.Mock(returncode=2)
        with mock.patch.object(launcher.subprocess, "run", return_value=fake), \
             contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaisesRegex(launcher.LaunchError, "coding remains OFF"):
                launcher.verify_execution_preflight()

    def test_preflight_timeout_denies_launch(self):
        with mock.patch.object(launcher.subprocess, "run",
                               side_effect=launcher.subprocess.TimeoutExpired("preflight", 1300)), \
             contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaisesRegex(launcher.LaunchError, "coding remains OFF"):
                launcher.verify_execution_preflight()

    def test_cached_build_kept_without_refresh(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            target = root / "app"
            (target / "wwwroot").mkdir(parents=True)
            (target / "CodingAssistant.Api").write_text("api")
            (target / "wwwroot/index.html").write_text("Mission Control")
            with mock.patch.object(launcher, "public_release", return_value=None):
                self.assertEqual(launcher.install_app(target, interactive=False), target)

    def test_offline_refresh_preserves_cached_build(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            target = root / "app"
            (target / "wwwroot").mkdir(parents=True)
            (target / "CodingAssistant.Api").write_text("api")
            (target / "wwwroot/index.html").write_text("Mission Control")
            with mock.patch.object(launcher.Path, "home", return_value=root), \
                 mock.patch.object(launcher, "public_release", return_value=None), \
                 mock.patch.object(launcher, "gh_download", return_value=False):
                self.assertEqual(launcher.install_app(target, interactive=False,
                                                      refresh=True), target)
            # Failure before package verification must preserve current version.
            self.assertTrue(launcher.valid_app(target))

    def test_release_checksum_installs_only_verified_zip(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            root.mkdir(exist_ok=True)
            archive = root / "app.zip"
            with zipfile.ZipFile(archive, "w") as zipped:
                zipped.writestr("CodingAssistant.Api", b"binary")
                zipped.writestr("wwwroot/index.html", "Mission Control")
            checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
            def fetch(url, target, limit):
                if url.endswith(".sha256"):
                    target.write_text(checksum + "  codingassistant-linux-x64.zip\\n")
                else:
                    target.write_bytes(archive.read_bytes())
            staging = root / "staging"
            staging.mkdir()
            sha = "a" * 40
            with mock.patch.object(launcher, "fetch_public_asset", side_effect=fetch):
                launcher.install_release((sha, "https://github.com/a.zip",
                                          "https://github.com/a.sha256"), staging)
            self.assertTrue(launcher.valid_app(staging))
            self.assertEqual((staging / ".release-commit").read_text().strip(), sha)

    def test_corrupt_release_never_installs(self):
        with tempfile.TemporaryDirectory() as temp:
            staging = pathlib.Path(temp) / "staging"
            staging.mkdir()
            def fetch(url, target, limit):
                if url.endswith(".sha256"):
                    target.write_text("0" * 64 + "  codingassistant-linux-x64.zip\\n")
                else:
                    target.write_bytes(b"not a verified application")
            with mock.patch.object(launcher, "fetch_public_asset", side_effect=fetch):
                with self.assertRaisesRegex(launcher.LaunchError, "checksum mismatch"):
                    launcher.install_release(("a"*40, "https://github.com/a.zip",
                                            "https://github.com/a.sha256"), staging)
            self.assertFalse(launcher.valid_app(staging))

    def test_matching_release_updates_existing_build_once(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            target = root / "app"
            (target / "wwwroot").mkdir(parents=True)
            (target / "CodingAssistant.Api").write_text("old")
            (target / "wwwroot/index.html").write_text("Mission Control")
            sha = "a" * 40
            release = (sha, "https://github.com/zip", "https://github.com/sha")
            def installer(release, staging):
                (staging / "CodingAssistant.Api").write_text("new")
                (staging / "wwwroot").mkdir()
                (staging / "wwwroot/index.html").write_text("Mission Control")
                (staging / ".release-commit").write_text(sha)
                return sha
            with mock.patch.object(launcher, "public_release", return_value=release), \
                 mock.patch.object(launcher, "checkout_commit", return_value=sha), \
                 mock.patch.object(launcher, "install_release", side_effect=installer) as install:
                self.assertEqual(launcher.install_app(target, interactive=False), target)
                self.assertEqual((target / "CodingAssistant.Api").read_text(), "new")
                self.assertEqual(launcher.install_app(target, interactive=False), target)
                install.assert_called_once()

    def test_release_failure_preserves_cached_binary(self):
        with tempfile.TemporaryDirectory() as temp:
            target = pathlib.Path(temp) / "app"
            (target / "wwwroot").mkdir(parents=True)
            (target / "CodingAssistant.Api").write_text("old")
            (target / "wwwroot/index.html").write_text("Mission Control")
            sha = "a" * 40
            with mock.patch.object(launcher, "public_release", return_value=(
                sha, "https://github.com/zip", "https://github.com/sha")), \
                 mock.patch.object(launcher, "checkout_commit", return_value=sha), \
                 mock.patch.object(launcher, "install_release",
                                   side_effect=launcher.LaunchError("bad checksum")):
                self.assertEqual(launcher.install_app(target, interactive=False), target)
                self.assertEqual((target / "CodingAssistant.Api").read_text(), "old")

    def test_desktop_shortcut_created_once_without_secrets(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            (root / "start.sh").write_text("#!/bin/bash")
            with mock.patch.object(launcher, "ROOT", root), \
                 mock.patch.object(launcher.Path, "home", return_value=root), \
                 mock.patch.dict(os.environ, {"DISPLAY": ":0"}):
                self.assertTrue(launcher.install_desktop_shortcut())
                shortcut = root / ".local/share/applications/codingassistant.desktop"
                data = shortcut.read_text()
                self.assertIn("Exec=/usr/bin/env bash ", data)
                self.assertIn("Terminal=true", data)
                self.assertNotIn("CODINGASSISTANT_OWNER_TOKEN", data)
                self.assertFalse(launcher.install_desktop_shortcut())

    def test_failed_preflight_opens_history_only(self):
        with mock.patch.object(launcher.sys, "platform", "linux"), \
             mock.patch.object(launcher.os, "geteuid", return_value=1000, create=True), \
             mock.patch.object(launcher, "free_port", return_value=True), \
             mock.patch.object(launcher, "install_desktop_shortcut", return_value=False), \
             mock.patch.object(launcher, "verify_execution_preflight",
                               side_effect=launcher.LaunchError("not safe")), \
             mock.patch.object(launcher, "install_app",
                               return_value=pathlib.Path("/fixture")), \
             mock.patch.object(launcher, "start") as start, \
             mock.patch.object(launcher.sys, "argv", ["launch.py", "--enable-execution"]):
            self.assertEqual(launcher.main(), 0)
            self.assertFalse(start.call_args.kwargs["enable_execution"])

    def test_occupied_port_never_launches_second_api(self):
        with mock.patch.object(launcher, "free_port", return_value=False), \
             mock.patch.object(launcher.subprocess, "Popen",
                               side_effect=AssertionError("API unexpectedly started")):
            with self.assertRaises(launcher.LaunchError):
                launcher.start(pathlib.Path("/fixture/CodingAssistant.Api"))


if __name__ == "__main__":
    unittest.main()
