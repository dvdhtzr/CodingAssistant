"""M0.16 real qualification policy tests (no Ollama or generated code)."""
from __future__ import annotations
import json
import pathlib
import shutil
import sys
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


if __name__ == "__main__":
    unittest.main()
