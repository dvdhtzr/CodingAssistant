"""Tests for the usable single-file MVP. No Ollama or Podman required."""
from __future__ import annotations
import argparse
import contextlib
import io
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "mvp"))
import assist


class MvpTests(unittest.TestCase):
    def test_non_traversing_file_selection_only(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Program.cs").write_text("class Program {}")
            self.assertEqual(assist.select_source(root, "Program.cs")[0],
                             Path("Program.cs"))
            for bad in ("../secret.cs", "/etc/secret.cs", "Program.json"):
                with self.subTest(bad=bad), self.assertRaises(ValueError):
                    assist.select_source(root, bad)
            (root / "link.cs").symlink_to(root / "Program.cs")
            with self.assertRaises(ValueError):
                assist.select_source(root, "link.cs")

    def test_stage_only_source_and_project_not_secrets(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, staged = root / "source", root / "staged"
            source.mkdir()
            (source / "Program.cs").write_text("class Program {}")
            (source / "Test.csproj").write_text("<Project/>")
            (source / ".env").write_text("PASSWORD=TOPSECRET")
            (source / "appsettings.json").write_text('{"secret":"TOPSECRET"}')
            (source / "bin").mkdir()
            (source / "bin" / "evil.cs").write_text("not source")
            (source / "outside.cs").symlink_to(source / "Program.cs")
            assist.stage_project(source, staged)
            self.assertEqual((staged / "Program.cs").read_text(), "class Program {}")
            self.assertTrue((staged / "Test.csproj").is_file())
            for excluded in (".env", "appsettings.json", "bin/evil.cs", "outside.cs"):
                self.assertFalse((staged / excluded).exists(), excluded)

    def test_fenced_and_raw_source_are_normalized(self):
        self.assertEqual(assist.normalize_proposal("class X {}\n"), "class X {}\n")
        fence = chr(96) * 3
        self.assertEqual(assist.normalize_proposal(fence + "csharp\nclass X {}\n" + fence),
                         "class X {}\n")
        for bad in ("", fence + "csharp\nunclosed", "abc\x00def"):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                assist.normalize_proposal(bad)

    def test_diff_requires_human_approval_and_does_not_edit_original(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            src = root / "original"
            src.mkdir()
            (src / "Program.cs").write_text("var count = 1;\n")
            dest = root / "artifacts"
            args = argparse.Namespace(model="qwen3.5:4b", demo=False,
                                      project=str(src), file="Program.cs",
                                      task="Fix count", build=False)
            with mock.patch.object(assist, "ARTIFACTS", dest), \
                 mock.patch.object(assist, "propose", return_value="var count = 2;\n"), \
                 mock.patch("builtins.input", return_value="APPLY"), \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(assist.run(args), 0)
            self.assertEqual((src / "Program.cs").read_text(), "var count = 1;\n")
            runs = list(dest.iterdir())
            self.assertEqual(len(runs), 1)
            self.assertEqual((runs[0] / "project/Program.cs").read_text(),
                             "var count = 2;\n")
            self.assertIn("+var count = 2;", (runs[0] / "change.patch").read_text())

    def test_rejection_does_not_stage_or_edit(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            src = root / "src"
            src.mkdir()
            (src / "Program.cs").write_text("original\n")
            dest = root / "artifacts"
            args = argparse.Namespace(model="qwen3.5:4b", demo=False,
                                      project=str(src), file="Program.cs",
                                      task="Change it", build=False)
            with mock.patch.object(assist, "ARTIFACTS", dest), \
                 mock.patch.object(assist, "propose", return_value="different\n"), \
                 mock.patch("builtins.input", return_value="no"), \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(assist.run(args), 1)
            self.assertFalse(dest.exists())
            self.assertEqual((src / "Program.cs").read_text(), "original\n")

    def test_unreviewably_large_diff_is_not_approved(self):
        before = "\n".join(str(i) for i in range(500)) + "\n"
        after = "\n".join("x" + str(i) for i in range(500)) + "\n"
        self.assertGreater(len(assist.diff_lines(before, after, "Program.cs")),
                           assist.MAX_DIFF_LINES)

    def test_model_is_exactly_pinned_and_never_emits_tools(self):
        catalog = {"models": [{"name": "qwen3.5:4b", "digest": "fixed"}]}
        response = {"model": "qwen3.5:4b",
                    "choices": [{"finish_reason": "stop",
                                 "message": {"content": "class Program {}\n"}}]}
        with mock.patch.object(assist, "json_http",
                               side_effect=[catalog, response, catalog]) as request:
            self.assertEqual(assist.propose("qwen3.5:4b", "Program.cs",
                                            "old\n", "Fix"), "class Program {}\n")
        self.assertEqual(request.call_args_list[1].args[0], "/v1/chat/completions")
        payload = request.call_args_list[1].args[1]
        self.assertEqual(payload["model"], "qwen3.5:4b")
        self.assertNotIn("tools", payload)
        self.assertEqual(payload["stream"], False)
        with mock.patch.object(assist, "json_http",
                               side_effect=[catalog, {
                                   **response, "choices": [{
                                       "finish_reason": "tool_calls",
                                       "message": {"content": "", "tool_calls": [{}]}}]}]):
            with self.assertRaises(ValueError):
                assist.propose("qwen3.5:4b", "Program.cs", "old", "Fix")

    def test_build_command_is_immutable_and_network_denied(self):
        with tempfile.TemporaryDirectory() as directory:
            for step in ("restore", "build", "run"):
                cmd = assist._container_command(Path(directory), "b" * 64,
                                                "Repair.csproj", step)
                for flag in ("--network=none", "--read-only", "--cap-drop=all",
                             "--security-opt=no-new-privileges", "--pull=never",
                             "--userns=keep-id"):
                    self.assertIn(flag, cmd)
                self.assertEqual(cmd.count("--volume"), 1)
                self.assertIn(":/workspace:rw", cmd[cmd.index("--volume") + 1])
                self.assertNotIn("--privileged", cmd)
                self.assertNotIn("--network=host", cmd)
                self.assertNotIn("shell", cmd)
                self.assertEqual(cmd[cmd.index("--entrypoint=dotnet") + 1], "b" * 64)
            with self.assertRaises(ValueError):
                assist._container_command(Path(directory), "mutable:latest",
                                          "Repair.csproj", "build")


if __name__ == "__main__":
    unittest.main()
