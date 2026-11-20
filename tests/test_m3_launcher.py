"""Model-free, cross-platform launcher tests. Never download, start .NET or invoke LLMs."""
import contextlib
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
        browser.assert_called_once_with("http://127.0.0.1:5098")

    def test_occupied_port_never_launches_second_api(self):
        with mock.patch.object(launcher, "free_port", return_value=False), \
             mock.patch.object(launcher.subprocess, "Popen",
                               side_effect=AssertionError("API unexpectedly started")):
            with self.assertRaises(launcher.LaunchError):
                launcher.start(pathlib.Path("/fixture/CodingAssistant.Api"))


if __name__ == "__main__":
    unittest.main()
