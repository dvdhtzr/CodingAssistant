"""M0.13 strict model/tool contract tests; never calls Ollama or Podman."""
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))

from pinned_real_edit import (
    EDIT_ARGS, MODEL_TASK, TOOL_ID, edit_completion, finish_completion,
    parse_tool_sse, validate_initial_request, validate_followup_request,
)
from real_opencode_edit_probe import CLIENT, EDIT_CHECKS, edit_command
from controlled_repair_pilot import exact_checks


def sse(chunks, model="qwen3.5:4b", done=True):
    data = b""
    for delta, reason in chunks:
        obj = {"model": model, "choices": [
            {"index": 0, "delta": delta, "finish_reason": reason}]}
        data += b"data: " + json.dumps(obj).encode() + b"\n\n"
    return data + (b"data: [DONE]\n\n" if done else b"")


def request():
    return {
        "model": "qwen3.5:4b", "stream": True,
        "messages": [{"role": "user", "content": MODEL_TASK}],
        "tools": [{"type": "function", "function": {"name": "edit",
            "parameters": {"type": "object", "properties": {
                "path": {"type": "string"}, "oldString": {"type": "string"},
                "newString": {"type": "string"}},
                "required": ["path", "oldString", "newString"]}}}],
    }


class RealOpenCodeEditContracts(unittest.TestCase):
    def test_valid_single_tool_call_and_fragmented_arguments(self):
        args = json.dumps(EDIT_ARGS)
        raw = sse([
            ({"role": "assistant"}, None),
            ({"tool_calls": [{"index": 0, "id": "model-generated",
               "type": "function", "function": {"name": "edit",
               "arguments": args[:11]}}]}, None),
            ({"tool_calls": [{"index": 0, "function": {
               "arguments": args[11:]}}]}, None),
            ({}, "tool_calls"),
        ])
        self.assertEqual(parse_tool_sse(raw, "qwen3.5:4b"), EDIT_ARGS)

    def test_unauthorized_edits_and_extra_tools_fail_closed(self):
        bad = [
            {"path": "/host/home/dave/secret", "oldString": "x", "newString": "y"},
            {"path": "/workspace/Repair.csproj", "oldString": "x", "newString": "y"},
            {"path": "/workspace/Program.cs", "oldString": "values.Summ()",
             "newString": "values.Sum(); System.IO.File.Delete(\"/etc/passwd\")"},
            {**EDIT_ARGS, "replaceAll": True},
        ]
        for args in bad:
            with self.subTest(args=args):
                payload = sse([({"tool_calls": [{"index": 0,
                    "function": {"name": "edit", "arguments": json.dumps(args)}}]}, None),
                               ({}, "tool_calls")])
                with self.assertRaises(ValueError):
                    parse_tool_sse(payload, "qwen3.5:4b")

    def test_multiple_tools_text_and_incomplete_provider_stream_fail(self):
        tool = {"index": 0, "function": {
            "name": "edit", "arguments": json.dumps(EDIT_ARGS)}}
        valid = [({"tool_calls": [tool]}, None), ({}, "tool_calls")]
        variants = [
            sse(valid, done=False),
            sse(valid, model="other:1"),
            sse([({"content": "I will execute code", "tool_calls": [tool]}, None),
                 ({}, "tool_calls")]),
            sse([({"tool_calls": [tool, {**tool, "index": 1}]}, None),
                 ({}, "tool_calls")]),
            sse([({"tool_calls": [tool]}, None), ({}, "stop")]),
            sse([({"tool_calls": [{**tool, "function": {
                "name": "shell", "arguments": json.dumps(EDIT_ARGS)}}]}, None),
                ({}, "tool_calls")]),
            sse([({"tool_calls": [tool]}, None), ({}, "tool_calls"),
                 ({}, "tool_calls")]),
        ]
        for raw in variants:
            with self.subTest(raw=raw[:90]):
                with self.assertRaises(ValueError):
                    parse_tool_sse(raw, "qwen3.5:4b")

    def test_first_request_requires_exact_task_pinned_model_and_edit_schema(self):
        self.assertTrue(validate_initial_request(request(), "qwen3.5:4b"))
        for attribute, value in [
            ("model", "foreign:1"), ("stream", False),
            ("tools", []), ("messages", [{"role": "user", "content": "Do anything"}])
        ]:
            modified = request()
            modified[attribute] = value
            self.assertFalse(validate_initial_request(modified, "qwen3.5:4b"))

    def test_followup_only_one_pinned_tool_id(self):
        base = request()
        base["messages"].append(
            {"role": "tool", "tool_call_id": TOOL_ID, "content": "Edit applied"})
        self.assertTrue(validate_followup_request(base, "qwen3.5:4b"))
        base["messages"][-1]["tool_call_id"] = "unknown"
        self.assertFalse(validate_followup_request(base, "qwen3.5:4b"))
        base["messages"][-1]["tool_call_id"] = TOOL_ID
        base["messages"].append(dict(base["messages"][-1]))
        self.assertFalse(validate_followup_request(base, "qwen3.5:4b"))

    def test_serialized_broker_responses_never_create_extra_tools(self):
        proposed = edit_completion("qwen3.5:4b")
        self.assertIn(b'"name":"edit"', proposed)
        self.assertIn(TOOL_ID.encode(), proposed)
        self.assertNotIn(b'"name":"shell"', proposed)
        self.assertEqual(proposed.count(b'[DONE]'), 1)
        final = finish_completion("qwen3.5:4b")
        self.assertIn(b"EDIT COMPLETE", final)
        self.assertNotIn(b"tool_calls", final)

    def test_rootless_command_mounts_only_socket_and_disposable_workspace(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            private = root / "private"
            workspace = root / "workspace"
            private.mkdir()
            workspace.mkdir()
            sentinel = root / "outside"
            sentinel.touch()
            cmd = edit_command("a"*64, 1001, 1001, private, workspace,
                               "qwen3.5:4b", sentinel)
            self.assertEqual(cmd.count("--volume"), 2)
            mounts = [cmd[i+1] for i, val in enumerate(cmd) if val == "--volume"]
            self.assertIn(str(private.resolve()) + ":/run/codingassistant:ro", mounts)
            self.assertIn(str(workspace.resolve()) + ":/workspace:rw", mounts)
            for flag in ("--network=none", "--read-only", "--cap-drop=all",
                         "--security-opt=no-new-privileges", "--pull=never",
                         "--userns=keep-id", "--workdir=/workspace"):
                self.assertIn(flag, cmd)
            self.assertNotIn("--privileged", cmd)
            self.assertNotIn("--network=host", cmd)
            self.assertEqual(cmd[cmd.index("--eval")+1], CLIENT.read_text())

    def test_strict_guest_checks_and_no_legacy_benchmark(self):
        self.assertTrue(exact_checks({"checks": {key: True for key in EDIT_CHECKS}},
                                     EDIT_CHECKS))
        for name in EDIT_CHECKS:
            checks = {key: True for key in EDIT_CHECKS}
            checks[name] = False
            self.assertFalse(exact_checks({"checks": checks}, EDIT_CHECKS))
        source = CLIENT.read_text()
        self.assertIn("call_m013_exact_edit", source)
        self.assertIn("ollama.sock", source)
        self.assertIn("no_extra_files", source)
        self.assertNotIn("benchmark.py", source)
        self.assertNotIn("--acknowledge-unisolated-m0-risk", source)

    @unittest.skipUnless(shutil.which("node"), "Node unavailable")
    def test_isolated_guest_script_parses(self):
        result = subprocess.run(["node", "--check", str(CLIENT)],
                                capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main()
