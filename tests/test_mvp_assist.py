"""Tests for the usable single-file MVP. No Ollama or Podman required."""
from __future__ import annotations
import argparse
import contextlib
import io
import subprocess
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

    def test_retry_uses_compiler_feedback_and_stages_a_new_approved_copy(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "artifacts"
            old = root / "20261119T010319Z-9726f2d8" / "project"
            old.mkdir(parents=True)
            (old / "Program.cs").write_text(
                "int x = 1;\nusing System.Linq;\nConsole.WriteLine(x);\n")
            (old / "Repair.csproj").write_text("<Project/>")
            patched = "using System.Linq;\nint x = 1;\nConsole.WriteLine(x);\n"
            args = argparse.Namespace(model="qwen3.5:4b", demo=False,
                                      project=None, file="Program.cs",
                                      task=None, build=False, run=True,
                                      retry="20261119T010319Z-9726f2d8")
            attempts = iter([False, True])
            def build(*args, **kwargs):
                if not next(attempts):
                    kwargs["feedback"].append(
                        "error CS1529: using directives must precede statements")
                    return False
                return True
            with mock.patch.object(assist, "ARTIFACTS", root), \
                 mock.patch.object(assist, "isolated_build", side_effect=build) as build_mock, \
                 mock.patch.object(assist, "propose", return_value=patched) as proposed, \
                 mock.patch("builtins.input", return_value="APPLY"), \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(assist.run(args), 0)
            self.assertIn("CS1529", proposed.call_args.args[3])
            self.assertIn("implicit usings", proposed.call_args.args[3])
            self.assertEqual(build_mock.call_count, 2)
            self.assertIn("using System.Linq;\nConsole", (old / "Program.cs").read_text())
            copies = [p for p in root.iterdir() if p.name != args.retry]
            self.assertEqual(len(copies), 1)
            self.assertEqual((copies[0] / "project/Program.cs").read_text(), patched)
            self.assertTrue((copies[0] / "change.patch").is_file())

    def test_retry_without_compiler_diagnostics_refuses_inference(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "artifacts"
            old = root / "20261119T010319Z-9726f2d8" / "project"
            old.mkdir(parents=True)
            (old / "Program.cs").write_text("some text")
            args = argparse.Namespace(model="qwen3.5:4b", demo=False,
                                      project=None, file="Program.cs",
                                      task=None, build=False, run=True,
                                      retry="20261119T010319Z-9726f2d8")
            with mock.patch.object(assist, "ARTIFACTS", root), \
                 mock.patch.object(assist, "isolated_build", return_value=False), \
                 mock.patch.object(assist, "propose") as model:
                with self.assertRaises(ValueError):
                    assist.run(args)
            model.assert_not_called()

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


    def test_automatic_compiler_repair_succeeds_after_one_approved_correction(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            original = root / "original"
            original.mkdir()
            (original / "Program.cs").write_text("int x = 0;\n")
            artifacts = root / "artifacts"
            args = argparse.Namespace(model="qwen3.5:4b", demo=False,
                                      project=str(original), file="Program.cs",
                                      task="Assign x correctly", build=True, run=False)

            proposals = ["int x = 1;\n", "int x = 2;\n"]
            def fake_propose(*args, **kwargs):
                kwargs["metadata"]["digest"] = "pinned-model-digest"
                return proposals.pop(0)

            def fake_build(*args, **kwargs):
                if not proposals:
                    return True
                kwargs["feedback"].append(
                    "/workspace/Program.cs(1,1): error CS1002: ; expected"
                )
                return False

            with mock.patch.object(assist, "ARTIFACTS", artifacts), \
                 mock.patch.object(assist, "propose", side_effect=fake_propose) as model, \
                 mock.patch.object(assist, "isolated_build", side_effect=fake_build) as build, \
                 mock.patch("builtins.input", side_effect=["APPLY", "APPLY"]) as approval, \
                 contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(assist.run(args), 0)

            self.assertEqual(model.call_count, 2)
            self.assertEqual(build.call_count, 2)
            self.assertEqual(approval.call_count, 2)
            self.assertEqual(model.call_args_list[1].kwargs["expected_digest"],
                             "pinned-model-digest")
            self.assertIn("CS1002", model.call_args_list[1].args[3])
            self.assertIn("Assign x correctly", model.call_args_list[1].args[3])
            self.assertIn("correction 1/2", output.getvalue())
            self.assertEqual((original / "Program.cs").read_text(), "int x = 0;\n")
            runs = sorted(artifacts.iterdir())
            self.assertEqual(len(runs), 2)
            results = [
                __import__("json").loads((item / "result.json").read_text())
                for item in runs
            ]
            self.assertEqual({item["status"] for item in results},
                             {"build_failed", "build_passed"})
            for item in results:
                self.assertEqual(item["model_digest"], "pinned-model-digest")
                self.assertTrue((artifacts / item["run_id"] / "change.patch").is_file())
            self.assertTrue(any(item["parent_run_id"] in
                                {other["run_id"] for other in results}
                                for item in results))

    def test_automatic_correction_rejection_keeps_approved_failed_copy(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            original = root / "src"
            original.mkdir()
            (original / "Program.cs").write_text("original\n")
            artifacts = root / "artifacts"
            args = argparse.Namespace(model="qwen3.5:4b", demo=False,
                                      project=str(original), file="Program.cs",
                                      task="Repair", build=True, run=False)
            def fails(*args, **kwargs):
                kwargs["feedback"].append("error CS1529: misplaced using")
                return False
            with mock.patch.object(assist, "ARTIFACTS", artifacts), \
                 mock.patch.object(assist, "propose",
                                   side_effect=["first\n", "second\n"]) as model, \
                 mock.patch.object(assist, "isolated_build", side_effect=fails), \
                 mock.patch("builtins.input", side_effect=["APPLY", "NO"]), \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(assist.run(args), 1)
            self.assertEqual(model.call_count, 2)
            self.assertEqual((original / "Program.cs").read_text(), "original\n")
            runs = list(artifacts.iterdir())
            self.assertEqual(len(runs), 1)
            self.assertEqual((runs[0] / "project/Program.cs").read_text(), "first\n")

    def test_automatic_retry_blocks_on_infrastructure_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            original = root / "src"
            original.mkdir()
            (original / "Program.cs").write_text("original\n")
            artifacts = root / "artifacts"
            args = argparse.Namespace(model="qwen3.5:4b", demo=False,
                                      project=str(original), file="Program.cs",
                                      task="Repair", build=True, run=False)
            with mock.patch.object(assist, "ARTIFACTS", artifacts), \
                 mock.patch.object(assist, "propose", return_value="new\n") as model, \
                 mock.patch.object(assist, "isolated_build", return_value=False), \
                 mock.patch("builtins.input", return_value="APPLY"), \
                 contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(assist.run(args), 2)
            self.assertEqual(model.call_count, 1)
            self.assertEqual(len(list(artifacts.iterdir())), 1)
            self.assertIn("without C# compiler diagnostics", output.getvalue())

    def test_automatic_retry_stops_when_errors_repeat(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            original = root / "src"
            original.mkdir()
            (original / "Program.cs").write_text("original\n")
            artifacts = root / "artifacts"
            args = argparse.Namespace(model="qwen3.5:4b", demo=False,
                                      project=str(original), file="Program.cs",
                                      task="Repair", build=True, run=False)
            def fails(*args, **kwargs):
                kwargs["feedback"].append("error CS1002: same error")
                return False
            with mock.patch.object(assist, "ARTIFACTS", artifacts), \
                 mock.patch.object(assist, "propose",
                                   side_effect=["first\n", "second\n"]) as model, \
                 mock.patch.object(assist, "isolated_build", side_effect=fails), \
                 mock.patch("builtins.input", return_value="APPLY"), \
                 contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(assist.run(args), 2)
            self.assertEqual(model.call_count, 2)
            self.assertIn("repeated without progress", output.getvalue())
            self.assertEqual(len(list(artifacts.iterdir())), 2)

    def test_automatic_retry_is_bounded_to_two_corrections(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            original = root / "src"
            original.mkdir()
            (original / "Program.cs").write_text("original\n")
            artifacts = root / "artifacts"
            args = argparse.Namespace(model="qwen3.5:4b", demo=False,
                                      project=str(original), file="Program.cs",
                                      task="Repair", build=True, run=False)
            errors = iter(["error CS1001: first", "error CS1002: second",
                           "error CS1003: third"])
            def fails(*args, **kwargs):
                kwargs["feedback"].append(next(errors))
                return False
            with mock.patch.object(assist, "ARTIFACTS", artifacts), \
                 mock.patch.object(assist, "propose",
                                   side_effect=["first\n", "second\n",
                                                "third\n"]) as model, \
                 mock.patch.object(assist, "isolated_build", side_effect=fails) as build, \
                 mock.patch("builtins.input", return_value="APPLY") as approval, \
                 contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(assist.run(args), 2)
            self.assertEqual(model.call_count, 3)
            self.assertEqual(build.call_count, 3)
            self.assertEqual(approval.call_count, 3)
            self.assertEqual(len(list(artifacts.iterdir())), 3)
            self.assertIn("limit of 2", output.getvalue())
            self.assertEqual((original / "Program.cs").read_text(), "original\n")

    def test_model_digest_must_remain_pinned_between_proposals(self):
        catalog = {"models": [{"name": "qwen3.5:4b", "digest": "new-digest"}]}
        with mock.patch.object(assist, "json_http", return_value=catalog) as request:
            with self.assertRaisesRegex(ValueError, "digest changed"):
                assist.propose("qwen3.5:4b", "Program.cs", "old", "Fix",
                               expected_digest="old-digest")
        self.assertEqual(request.call_count, 1)


    def test_manual_retry_rejects_symlinked_saved_project_and_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            artifacts = root / "artifacts"
            run_id = "20261119T010319Z-9726f2d8"
            run_dir = artifacts / run_id
            run_dir.mkdir(parents=True)
            external = root / "outside"
            external.mkdir()
            (external / "Program.cs").write_text("keep\n")
            (run_dir / "project").symlink_to(external, target_is_directory=True)
            args = argparse.Namespace(model="qwen3.5:4b", demo=False,
                                      project=None, file="Program.cs", task=None,
                                      build=False, run=False, retry=run_id)
            with mock.patch.object(assist, "ARTIFACTS", artifacts), \
                 mock.patch.object(assist, "isolated_build") as build, \
                 mock.patch.object(assist, "propose") as model:
                with self.assertRaisesRegex(ValueError, "ordinary directory"):
                    assist.run(args)
                build.assert_not_called()
                model.assert_not_called()

            (run_dir / "project").unlink()
            (run_dir / "project").mkdir()
            (run_dir / "project/Program.cs").write_text("old\n")
            (run_dir / "result.json").symlink_to(external / "Program.cs")
            with mock.patch.object(assist, "ARTIFACTS", artifacts), \
                 mock.patch.object(assist, "isolated_build") as build, \
                 mock.patch.object(assist, "propose") as model:
                with self.assertRaisesRegex(ValueError, "must not be a symlink"):
                    assist.run(args)
                build.assert_not_called()
                model.assert_not_called()


    def test_custom_project_run_executes_isolated_acceptance_checks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            project = root / "project"
            project.mkdir()
            (project / "PricingCalculator.cs").write_text("public class PricingCalculator {}\n")
            (project / "OrderPricing.csproj").write_text("<Project/>")
            (project / "Program.cs").write_text("System.Console.WriteLine(\"PASS\");\n")
            artifacts = root / "artifacts"
            args = argparse.Namespace(model="qwen3.5:4b", demo=False,
                                      project=str(project), file="PricingCalculator.cs",
                                      task="Fix pricing", build=False, run=True)
            with mock.patch.object(assist, "ARTIFACTS", artifacts), \
                 mock.patch.object(assist, "propose",
                                   return_value="public class PricingCalculator { }\n"), \
                 mock.patch.object(assist, "isolated_build",
                                   return_value=True) as isolated, \
                 mock.patch("builtins.input", return_value="APPLY"), \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(assist.run(args), 0)
            self.assertEqual(isolated.call_count, 1)
            self.assertTrue(isolated.call_args.args[1])
            staged = isolated.call_args.args[0]
            self.assertNotEqual(staged, project)
            self.assertTrue((staged / "OrderPricing.csproj").is_file())
            self.assertTrue((staged / "Program.cs").is_file())
            self.assertEqual((project / "PricingCalculator.cs").read_text(),
                             "public class PricingCalculator {}\n")
            result = __import__("json").loads((staged.parent / "result.json").read_text())
            self.assertEqual(result["status"], "run_passed")

    def test_custom_project_runtime_failure_does_not_trigger_compiler_retry(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            project = root / "project"
            project.mkdir()
            (project / "PricingCalculator.cs").write_text("class P {}\n")
            (project / "OrderPricing.csproj").write_text("<Project/>")
            artifacts = root / "artifacts"
            args = argparse.Namespace(model="qwen3.5:4b", demo=False,
                                      project=str(project), file="PricingCalculator.cs",
                                      task="Fix pricing", build=False, run=True)
            with mock.patch.object(assist, "ARTIFACTS", artifacts), \
                 mock.patch.object(assist, "propose",
                                   return_value="class P { }\n") as model, \
                 mock.patch.object(assist, "isolated_build", return_value=False), \
                 mock.patch("builtins.input", return_value="APPLY"), \
                 contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(assist.run(args), 2)
            self.assertEqual(model.call_count, 1)
            self.assertIn("no automatic retry", output.getvalue())
            result_file = next(artifacts.iterdir()) / "result.json"
            record = __import__("json").loads(result_file.read_text())
            self.assertEqual(record["status"], "verification_failed")

    def test_cli_allows_custom_project_explicit_run(self):
        command = ["assist.py", "--project", "/tmp/example", "--file",
                   "PricingCalculator.cs", "--task", "Correct tax", "--run"]
        with mock.patch.object(assist.sys, "argv", command), \
             mock.patch.object(assist, "run", return_value=0) as run:
            self.assertEqual(assist.main(), 0)
            self.assertTrue(run.call_args.args[0].run)


    def test_failed_executable_assertions_are_captured_without_compiler_noise(self):
        with tempfile.TemporaryDirectory() as directory:
            project = Path(directory)
            (project / "OrderPricing.csproj").write_text("<Project/>")
            image = subprocess.CompletedProcess([], 0, "a" * 64, "")
            restore = subprocess.CompletedProcess([], 0, "Restore succeeded", "")
            build = subprocess.CompletedProcess([], 0, "Build succeeded", "")
            run = subprocess.CompletedProcess([], 1,
                "PASS: quantity\n"
                "FAIL: tax: InvalidOperationException: expected 3.15, got 3.50\n"
                "ORDER-PRICING ACCEPTANCE: 9/10 checks passed\n", 
                "An issue was encountered verifying workloads.\n")
            with mock.patch.object(assist.shutil, "which", return_value="/usr/bin/podman"), \
                 mock.patch.object(assist.os, "getuid", return_value=1000), \
                 mock.patch.object(assist.os, "getgid", return_value=1000), \
                 mock.patch.object(assist.sys, "platform", "linux"), \
                 mock.patch.object(assist.subprocess, "run",
                                   side_effect=[image, restore, build, run]) as process, \
                 contextlib.redirect_stdout(io.StringIO()):
                compiler, failed = [], []
                self.assertFalse(assist.isolated_build(project, True,
                                   feedback=compiler, test_feedback=failed))
            self.assertEqual(compiler, [])
            self.assertEqual(failed, [
                "FAIL: tax: InvalidOperationException: expected 3.15, got 3.50",
                "ORDER-PRICING ACCEPTANCE: 9/10 checks passed"])
            self.assertEqual(process.call_count, 4)

    def test_non_test_runtime_failure_does_not_generate_model_feedback(self):
        with tempfile.TemporaryDirectory() as directory:
            project = Path(directory)
            (project / "OrderPricing.csproj").write_text("<Project/>")
            results = [
                subprocess.CompletedProcess([], 0, "a" * 64, ""),
                subprocess.CompletedProcess([], 0, "", ""),
                subprocess.CompletedProcess([], 0, "", ""),
                subprocess.CompletedProcess([], 1, "", "Process crashed without tests"),
            ]
            with mock.patch.object(assist.shutil, "which", return_value="podman"), \
                 mock.patch.object(assist.os, "getuid", return_value=1000), \
                 mock.patch.object(assist.os, "getgid", return_value=1000), \
                 mock.patch.object(assist.sys, "platform", "linux"), \
                 mock.patch.object(assist.subprocess, "run", side_effect=results), \
                 contextlib.redirect_stdout(io.StringIO()):
                failed = []
                self.assertFalse(assist.isolated_build(project, True,
                                                       test_feedback=failed))
            self.assertEqual(failed, [])

    def test_acceptance_failures_trigger_automatic_approved_correction(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "src"
            source.mkdir()
            (source / "PricingCalculator.cs").write_text("class Pricing { /*old*/ }\n")
            (source / "OrderPricing.csproj").write_text("<Project/>")
            artifacts = root / "artifacts"
            args = argparse.Namespace(model="qwen3.5:4b", demo=False,
                project=str(source), file="PricingCalculator.cs",
                task="Correct tax; subtotal must remain undiscounted.",
                build=False, run=True)
            proposals = iter(["class Pricing { /*wrong*/ }\n",
                              "class Pricing { /*correct*/ }\n"])
            def model(*a, **k):
                k["metadata"]["digest"] = "pinned"
                return next(proposals)
            attempts = iter([False, True])
            def build(*a, **k):
                if not next(attempts):
                    k["test_feedback"].extend([
                        "FAIL: subtotal: expected 54.98, got 49.48",
                        "FAIL: tax: expected 3.15, got 3.50"])
                    return False
                return True
            with mock.patch.object(assist, "ARTIFACTS", artifacts), \
                 mock.patch.object(assist, "propose", side_effect=model) as proposed, \
                 mock.patch.object(assist, "isolated_build", side_effect=build) as isolated, \
                 mock.patch("builtins.input", side_effect=["APPLY", "APPLY"]) as approval, \
                 contextlib.redirect_stdout(io.StringIO()) as out:
                self.assertEqual(assist.run(args), 0)
            self.assertEqual(approval.call_count, 2)
            self.assertEqual(proposed.call_count, 2)
            self.assertEqual(isolated.call_count, 2)
            self.assertIn("expected 54.98", proposed.call_args_list[1].args[3])
            self.assertIn("subtotal must remain undiscounted",
                          proposed.call_args_list[1].args[3])
            self.assertEqual(proposed.call_args_list[1].kwargs["expected_digest"],
                             "pinned")
            self.assertIn("acceptance tests failed", out.getvalue())
            self.assertEqual((source / "PricingCalculator.cs").read_text(),
                             "class Pricing { /*old*/ }\n")
            records = [__import__("json").loads((p / "result.json").read_text())
                       for p in artifacts.iterdir()]
            self.assertEqual({r["status"] for r in records},
                             {"verification_failed", "run_passed"})
            self.assertEqual(sum(len(r["test_failures"]) for r in records), 2)
            self.assertEqual(sum(bool(r["parent_run_id"]) for r in records), 1)

    def test_manual_retry_resumes_previous_test_failure_in_new_copy(self):
        with tempfile.TemporaryDirectory() as directory:
            artifacts = Path(directory) / "artifacts"
            run_id = "20261119T032101Z-1cb9e2cf"
            prior = artifacts / run_id
            (prior / "project").mkdir(parents=True)
            (prior / "project/PricingCalculator.cs").write_text(
                "class Pricing { /*incorrect*/ }\n")
            (prior / "project/OrderPricing.csproj").write_text("<Project/>")
            (prior / "result.json").write_text(
                '{"model":"qwen3.5:4b","model_digest":"pinned","file":'
                '"PricingCalculator.cs","status":"verification_failed"}')
            args = argparse.Namespace(model="qwen3.5:4b", demo=False,
                project=None, retry=run_id, file="PricingCalculator.cs",
                task="Keep the pre-discount subtotal; only discount taxable base for tax.",
                build=False, run=True)
            calls = iter(["compiles", "fails-tests", "passes"])
            def build(*a, **k):
                state = next(calls)
                if state == "fails-tests":
                    k["test_feedback"].append(
                        "FAIL: subtotal: expected 54.98, got 49.48")
                    return False
                return True
            with mock.patch.object(assist, "ARTIFACTS", artifacts), \
                 mock.patch.object(assist, "isolated_build", side_effect=build) as isolated, \
                 mock.patch.object(assist, "propose",
                     return_value="class Pricing { /*correct*/ }\n") as proposed, \
                 mock.patch("builtins.input", return_value="APPLY"), \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(assist.run(args), 0)
            self.assertEqual(isolated.call_count, 3)
            self.assertIn("expected 54.98", proposed.call_args.args[3])
            self.assertEqual(proposed.call_args.kwargs["expected_digest"], "pinned")
            self.assertIn("/*incorrect*/",
                          (prior / "project/PricingCalculator.cs").read_text())
            copies = [p for p in artifacts.iterdir() if p.name != run_id]
            self.assertEqual(len(copies), 1)
            record = __import__("json").loads((copies[0] / "result.json").read_text())
            self.assertEqual(record["parent_run_id"], run_id)
            self.assertEqual(record["status"], "run_passed")

    def test_test_feedback_never_overrides_rejection(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            project = root / "src"
            project.mkdir()
            (project / "PricingCalculator.cs").write_text("old\n")
            (project / "OrderPricing.csproj").write_text("<Project/>")
            args = argparse.Namespace(model="qwen3.5:4b", demo=False,
                project=str(project), file="PricingCalculator.cs",
                task="Repair logic", build=False, run=True)
            def fails(*a, **k):
                k["test_feedback"].append("FAIL: tax: expected 3.15, got 3.50")
                return False
            with mock.patch.object(assist, "ARTIFACTS", root / "artifacts"), \
                 mock.patch.object(assist, "isolated_build", side_effect=fails), \
                 mock.patch.object(assist, "propose",
                                   side_effect=["wrong\n", "better\n"]) as model, \
                 mock.patch("builtins.input", side_effect=["APPLY", "NO"]), \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(assist.run(args), 1)
            self.assertEqual(model.call_count, 2)
            self.assertEqual((project / "PricingCalculator.cs").read_text(), "old\n")
            self.assertEqual(len(list((root / "artifacts").iterdir())), 1)

    def test_repeated_test_failures_stop_bounded_recovery(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            project = root / "src"
            project.mkdir()
            (project / "PricingCalculator.cs").write_text("old\n")
            (project / "OrderPricing.csproj").write_text("<Project/>")
            args = argparse.Namespace(model="qwen3.5:4b", demo=False,
                project=str(project), file="PricingCalculator.cs",
                task="Fix", build=False, run=True)
            def fails(*a, **k):
                k["test_feedback"].append("FAIL: tax: expected 3.15, got 3.50")
                return False
            with mock.patch.object(assist, "ARTIFACTS", root / "artifacts"), \
                 mock.patch.object(assist, "isolated_build", side_effect=fails), \
                 mock.patch.object(assist, "propose",
                                   side_effect=["wrong\n", "still wrong\n"]) as model, \
                 mock.patch("builtins.input", return_value="APPLY"), \
                 contextlib.redirect_stdout(io.StringIO()) as out:
                self.assertEqual(assist.run(args), 2)
            self.assertEqual(model.call_count, 2)
            self.assertIn("repeated without progress", out.getvalue())


if __name__ == "__main__":
    unittest.main()
