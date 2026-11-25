"""M0.16 real qualification policy tests (no Ollama or generated code)."""
from __future__ import annotations
import hashlib
import json
import pathlib
import shutil
import sys
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))

import pinned_qualification as q
import real_qualification as runner
from qualify import TASKS


def stream(model: str, args: dict, name: str = "write") -> bytes:
    def event(delta: dict, finish=None):
        return ("data: " + json.dumps({"model": model,
                "choices": [{"index": 0, "delta": delta,
                             "finish_reason": finish}]}) + "\n\n").encode()
    return (event({"role": "assistant"}) +
            event({"tool_calls": [{"index": 0, "type": "function",
                                   "function": {
                                       "name": name, "arguments": json.dumps(args)}}]}) +
            event({}, "tool_calls") + b"data: [DONE]\n\n")


class QualificationBrokerTests(unittest.TestCase):
    def test_real_tool_stream_parsing(self):
        args = {"path": "/workspace/api/Program.cs", "content": "class X {}"}
        self.assertEqual(q.parse_write_sse(stream("qwen3.5:4b", args),
                                           "qwen3.5:4b"), args)
        self.assertEqual(q.tool_id(0), "call_m016_0")
        for bad in (
            stream("other:4b", args),
            stream("qwen3.5:4b", args, "shell"),
            stream("qwen3.5:4b", {"path": "../.ssh/key", "content": ""}),
            stream("qwen3.5:4b", {"path": "/workspace/api/Program.cs",
                                    "content": "x\x00y"}),
            stream("qwen3.5:4b", {"path": "/workspace/api/Program.cs",
                                    "content": "X" * 17000}),
            stream("qwen3.5:4b", args).replace(b"data: [DONE]", b"data: []"),
        ):
            with self.subTest(bad=bad[:50]):
                with self.assertRaises(ValueError):
                    q.parse_write_sse(bad, "qwen3.5:4b")

    def test_exact_tool_call_and_message_contract(self):
        model = "qwen3.5:4b"
        for task in TASKS:
            with self.subTest(task=task):
                request = {
                    "model": model, "stream": True,
                    "messages": [{"role": "user", "content": q.task_prompt(task)}],
                    "tools": [{"type": "function", "function": {
                        "name": "write",
                        "parameters": {"properties": {
                            "path": {"type": "string"},
                            "content": {"type": "string"}},
                            "required": ["path", "content"]}}}],
                }
                data = json.dumps(request).encode()
                self.assertTrue(q.validate_chat(data, task, model, 0))
                self.assertFalse(q.validate_chat(data, task, "foreign:1", 0))
                self.assertFalse(q.validate_chat(data, task, model, 1))
                request["messages"].append({
                    "role": "tool", "tool_call_id": q.tool_id(0),
                    "content": "Wrote file successfully"})
                self.assertTrue(q.validate_chat(json.dumps(request).encode(),
                                                task, model, 1))
                request["messages"][-1]["tool_call_id"] = "wrong"
                self.assertFalse(q.validate_chat(json.dumps(request).encode(),
                                                 task, model, 1))
                self.assertEqual(len(q.task_prompt(task)) > 40, True)

    def test_frozen_task_specific_paths(self):
        self.assertEqual(set(q.FILES), set(TASKS))
        self.assertEqual(set(q.FILES[TASKS[2]]),
                         {"/workspace/api/Program.cs",
                          "/workspace/web/src/TaskSummary.tsx"})
        for task in TASKS:
            self.assertEqual(set(q.FILES[task]).issubset(set(q.READS[task])),
                             task != TASKS[2])

    def test_disposable_command_exact_two_mounts(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            for folder in ("workspace", "private"):
                (root / folder).mkdir()
            sentinel = root / "host-only"
            sentinel.write_text("hidden")
            cmd = runner.qualification_command(
                "a" * 64, 1001, 1001, root / "private",
                root / "workspace", sentinel, "qwen3.5:4b", TASKS[2])
            self.assertEqual(cmd.count("--volume"), 2)
            self.assertIn(f"{(root / 'workspace').resolve()}:/workspace:rw", cmd)
            self.assertIn(f"{(root / 'private').resolve()}:/run/codingassistant:ro", cmd)
            for flag in ("--network=none", "--read-only", "--cap-drop=all",
                         "--security-opt=no-new-privileges", "--pull=never",
                         "--userns=keep-id", "--memory=3g"):
                self.assertIn(flag, cmd)
            self.assertEqual(cmd[-5:][0], "--")
            self.assertEqual(cmd[-4:][0], "qwen3.5:4b")
            self.assertEqual(cmd[-3], TASKS[2])
            self.assertEqual(cmd[-2], q.task_prompt(TASKS[2]))
            self.assertEqual(cmd[-1], str(sentinel.resolve()))

    def test_bounded_compiler_repair_command_and_reject_mutation(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            (root / "workspace").mkdir()
            (root / "private").mkdir()
            sentinel = root / "host-only"
            sentinel.write_text("hidden")
            errors = ("CS0246: The type or namespace name 'FromQuery' could not be found",)
            cmd = runner.qualification_command(
                "a" * 64, 1001, 1001, root / "private",
                root / "workspace", sentinel, "qwen3.5:4b", TASKS[0],
                errors)
            self.assertEqual(cmd.count("--volume"), 2)
            self.assertEqual(cmd[-3], TASKS[0])
            self.assertIn(q.task_prompt(TASKS[0]), cmd[-2])
            self.assertIn("CS0246", cmd[-2])
            with self.assertRaises(ValueError):
                runner.qualification_command(
                    "a" * 64, 1001, 1001, root / "private",
                    root / "workspace", sentinel, "qwen3.5:4b", TASKS[2],
                    errors)
            self.assertTrue(q.authorize_write({
                "path": q.FILES[TASKS[0]][0],
                "content": "var app = 1;"}, TASKS[0], []))
            self.assertFalse(q.authorize_write({
                "path": "/etc/passwd", "content": "x"}, TASKS[0], []))
            self.assertFalse(q.authorize_write({
                "path": q.FILES[TASKS[0]][0], "content": "x"}, TASKS[0],
                [{"path": q.FILES[TASKS[0]][0], "content": "old"}]))

    def test_workspace_extra_files_symlinks_and_outside_dirs_denied(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = pathlib.Path(temp) / "source"
            shutil.copytree(runner.FIXTURES / TASKS[2], folder)
            before = runner.scan_workspace(folder, TASKS[2])
            self.assertEqual(set(before), runner.allowed_source_files(TASKS[2]) -
                             {"web/src/TaskSummary.tsx"})
            (folder / "web/src/TaskSummary.tsx").write_text("export {}")
            self.assertIn("web/src/TaskSummary.tsx",
                          runner.scan_workspace(folder, TASKS[2]))
            extra = folder / "web/src/secret.ts"
            extra.write_text("x")
            with self.assertRaises(ValueError):
                runner.scan_workspace(folder, TASKS[2])
            extra.unlink()
            try:
                extra.symlink_to("/etc/passwd")
            except OSError:
                self.skipTest("Symlinks disabled")
            with self.assertRaises(ValueError):
                runner.scan_workspace(folder, TASKS[2])
            extra.unlink()
            (folder / "web/src/TaskSummary.tsx").unlink()
            self.assertEqual(runner.scan_workspace(folder, TASKS[2]), before)

    def test_safety_fields_not_assumed(self):
        source = pathlib.Path(runner.__file__).read_text(encoding="utf-8")
        for field in ("unauthorized_operation", "false_verification_claim",
                      "unrecoverable_execution_failure"):
            self.assertIn('"' + field + '": None', source)
        self.assertNotIn("--acknowledge-unisolated-m0-risk", source)
        self.assertNotIn("subprocess.Popen(['dotnet'", source)

    def test_fail_closed_on_non_linux(self):
        with patch.object(runner.sys, "platform", "win32"):
            with self.assertRaises(RuntimeError):
                runner.preflight("qwen3.5:4b")


class EvaluatorProvisioningTests(unittest.TestCase):
    def test_build_uses_inspected_id_and_only_reviewed_inputs(self):
        base_id, built_id = "a" * 64, "b" * 64
        contexts = []

        def inspect(image, timeout):
            expected = base_id if image == runner.RUNTIME_IMAGE else built_id
            self.assertIn(image, (runner.RUNTIME_IMAGE, runner.EVAL_IMAGE, built_id))
            return {"present": True, "image_id": "sha256:" + expected}

        def build(argv, **kwargs):
            self.assertEqual(argv[:3], ["podman", "build", "--pull=never"])
            self.assertEqual(argv[argv.index("--build-arg") + 1], "BASE_IMAGE=" + base_id)
            context = pathlib.Path(argv[-1])
            contexts.append(context)
            self.assertEqual({p.relative_to(context).as_posix()
                              for p in context.rglob("*") if p.is_file()},
                             set(runner.EVALUATOR_INPUTS))
            self.assertNotEqual(context, runner.ROOT)
            for relative in runner.EVALUATOR_INPUTS:
                self.assertEqual((context / relative).read_bytes(),
                                 (runner.ROOT / relative).read_bytes())
            pathlib.Path(argv[argv.index("--iidfile") + 1]).write_text(
                "sha256:" + built_id + "\n", encoding="ascii")
            return subprocess.CompletedProcess(argv, 0, "", "")

        with (patch.object(runner.sys, "platform", "linux"),
              patch.object(runner.os, "geteuid", return_value=1000, create=True),
              patch.object(runner, "inspect_podman", return_value={"rootless": True}),
              patch.object(runner, "image_status", side_effect=inspect) as inspect_call,
              patch.object(runner.subprocess, "run", side_effect=build)):
            report = runner.provision_evaluator()
        self.assertEqual(report["status"], "PROVISIONED")
        self.assertEqual(report["runtime_image_id"], base_id)
        self.assertEqual(report["evaluator_image_id"], built_id)
        self.assertEqual(report["recipe_inputs_sha256"], {
            relative: hashlib.sha256((runner.ROOT / relative).read_bytes()).hexdigest()
            for relative in runner.EVALUATOR_INPUTS})
        self.assertEqual([c.args[0] for c in inspect_call.call_args_list],
                         [runner.RUNTIME_IMAGE, built_id, runner.EVAL_IMAGE])
        self.assertFalse(contexts[0].exists())

    def test_mutable_and_malformed_base_ids_block_before_build(self):
        for bad in (None, "latest", runner.RUNTIME_IMAGE, "a" * 12, "a" * 63,
                    "sha256:" + "z" * 64):
            with self.subTest(bad=bad):
                with (patch.object(runner.sys, "platform", "linux"),
                      patch.object(runner.os, "geteuid", return_value=1000, create=True),
                      patch.object(runner, "inspect_podman", return_value={"rootless": True}),
                      patch.object(runner, "image_status",
                                   return_value={"present": True, "image_id": bad}),
                      patch.object(runner.subprocess, "run") as build):
                    with self.assertRaises(RuntimeError):
                        runner.provision_evaluator()
                    build.assert_not_called()

    def test_missing_or_substituted_build_identity_blocks(self):
        base, built, substituted = "a" * 64, "b" * 64, "c" * 64
        for receipt, exact, tagged in (
            (None, built, built), ("latest", built, built),
            (base, base, base), (built, substituted, built),
            (built, built, substituted), (built, None, built),
        ):
            with self.subTest(receipt=receipt, exact=exact, tagged=tagged):
                def inspect(image, timeout):
                    value = (base if image == runner.RUNTIME_IMAGE else
                             tagged if image == runner.EVAL_IMAGE else exact)
                    return {"present": value is not None, "image_id": value}

                def build(argv, **kwargs):
                    if receipt is not None:
                        pathlib.Path(argv[argv.index("--iidfile") + 1]).write_text(
                            receipt, encoding="ascii")
                    return subprocess.CompletedProcess(argv, 0, "", "")

                with (patch.object(runner.sys, "platform", "linux"),
                      patch.object(runner.os, "geteuid", return_value=1000, create=True),
                      patch.object(runner, "inspect_podman", return_value={"rootless": True}),
                      patch.object(runner, "image_status", side_effect=inspect),
                      patch.object(runner.subprocess, "run", side_effect=build)):
                    with self.assertRaises(RuntimeError):
                        runner.provision_evaluator()


if __name__ == "__main__":
    unittest.main()
