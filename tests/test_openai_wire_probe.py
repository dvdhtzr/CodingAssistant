"""Model-free negative contracts for M0.8a Ollama OpenAI wire preflight."""
import io
import json
import pathlib
import sys
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))
from openai_wire_probe import (HOST, PORT, MAX_LINE, PROMPT, TOOL,
                               payload, read_sse, complete, probe)

MODEL = "qwen3.5:4b"


def evt(delta, finish=None, model=MODEL, index=0):
    return ("data: " + json.dumps({"model": model, "choices": [{"index": index,
        "delta": delta, "finish_reason": finish}]}) + "\n\n").encode()


def good():
    return b"".join([
        evt({"role": "assistant"}),
        evt({"tool_calls": [{"index": 0, "function": {
            "name": TOOL, "arguments": '{"stat'}}]}),
        evt({"tool_calls": [{"index": 0, "function": {
            "arguments": 'us":"READY"}'}}]}, "tool_calls"),
        b"data: [DONE]\n\n",
    ])


class WireTests(unittest.TestCase):
    def test_fixed_pinned_non_executable_request(self):
        req = payload(MODEL)
        self.assertEqual(req["model"], MODEL)
        self.assertEqual(req["messages"], [{"role": "user", "content": PROMPT}])
        self.assertTrue(req["stream"])
        self.assertEqual([t["function"]["name"] for t in req["tools"]], [TOOL])
        self.assertEqual(req["tools"][0]["function"]["parameters"]["properties"]["status"]["enum"], ["READY"])
        self.assertNotIn("tool_choice", req)  # Not supported by Ollama's documented subset.

    def test_invalid_model_identifiers_denied(self):
        for tag in ("", "ollama/qwen3.5:4b", "../../etc/passwd", "my:model:extra",
                    "qwen3.5:4b\nHost: outside", "https://example.com"):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                payload(tag)

    def test_fragmented_valid_tool_call(self):
        self.assertEqual(read_sse(io.BytesIO(good()), MODEL), 3)

    def test_foreign_unfinished_or_oversized_stream_denied(self):
        cases = [b"data: [DONE]\n", evt({"role": "assistant"}),
                 evt({"tool_calls": [{"index": 1, "function": {"name": TOOL}}]})+b"data: [DONE]\n",
                 evt({"tool_calls": [{"index": 0, "function": {"name": "shell"}}]})+b"data: [DONE]\n",
                 evt({"role": "assistant"}, model="other:1")+b"data: [DONE]\n",
                 evt({"role": "assistant"}, index=1)+b"data: [DONE]\n",
                 b"bad\n"+good(), b"data: "+b"x"*(MAX_LINE+1)+b"\n",
                 good().replace(b'READY', b'FAIL'),
                 good().replace(b'"tool_calls"}', b'"stop"}')]
        for data in cases:
            with self.subTest(data=data[:48]), self.assertRaises(ValueError):
                read_sse(io.BytesIO(data), MODEL)

    def test_only_loopback_fixed_post_to_inference(self):
        with mock.patch("openai_wire_probe.http.client.HTTPConnection") as conn:
            response = conn.return_value.getresponse.return_value
            response.status = 200
            response.getheader.return_value = "text/event-stream"
            response.readline.side_effect = io.BytesIO(good()).readlines() + [b""]
            self.assertEqual(complete(MODEL), 3)
            conn.assert_called_once_with(HOST, PORT, timeout=300)
            args = conn.return_value.request.call_args
            self.assertEqual(args.args[:2], ("POST", "/v1/chat/completions"))
            self.assertEqual(json.loads(args.kwargs["body"]), payload(MODEL))
            self.assertNotIn("Authorization", args.kwargs["headers"])

    def test_no_false_success_if_tool_call_absent(self):
        with mock.patch("openai_wire_probe.inspect", return_value=("pin", {
            "exact_model": True, "native_tools": True, "openai_catalog": True})), \
             mock.patch("openai_wire_probe.complete", side_effect=ValueError("no tool call")):
            report = probe(MODEL)
        self.assertEqual(report["status"], "BLOCKED")
        self.assertFalse(report["checks"]["sse_tool_call"])


if __name__ == "__main__":
    unittest.main()
