"""M0.11 proposal-only broker: model-free positive/negative contracts."""
import http.client
import json
import os
import pathlib
import socket
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))
from disposable_workspace_probe import SOURCE
from ollama_bridge import serve_bridge
from pinned_repair_proposal import (
    frozen_prompt, validate_candidate, validate_chat_request, parse_proposal_sse,
    request_repair_proposal, pinned_repair_handler,
)

MODEL, DIGEST = "qwen3.5:4b", "d"*64
ORIGINAL = (SOURCE / "Program.cs").read_text(encoding="utf-8")
FIXED = ORIGINAL.replace("values.Summ()", "values.Sum()")
PROMPT = frozen_prompt()


def request_data(model=MODEL, prompt=PROMPT):
    return json.dumps({"model": model, "stream": True,
        "messages": [
            {"role": "system", "content": "OpenCode internals not forwarded"},
            {"role": "user", "content": prompt},
        ],
        "tools": [{"type": "function", "function": {"name": "shell"}}]
    }).encode()


def events(content=FIXED, model=MODEL, finish="stop", tool=False):
    chunks = [
        {"model": model, "choices": [{
            "index": 0, "delta": {"role": "assistant"}, "finish_reason": None}]},
        {"model": model, "choices": [{
            "index": 0,
            "delta": ({"tool_calls": [{"index": 0, "function": {
                "name": "shell", "arguments": "{}"}}]} if tool else
                {"content": content}), "finish_reason": None}]},
        {"model": model, "choices": [{
            "index": 0, "delta": {}, "finish_reason": finish}]},
    ]
    return (b"".join(b"data: " + json.dumps(x).encode() + b"\n\n"
                     for x in chunks) + b"data: [DONE]\n\n")


class RepairProposalTests(unittest.TestCase):
    def test_freeze_prompt_includes_broken_source_without_answer(self):
        self.assertIn(ORIGINAL, PROMPT)
        self.assertNotIn("values.Sum()", PROMPT)
        self.assertIn("Total: 15", PROMPT)

    def test_only_exact_minimal_repair_accepted(self):
        self.assertEqual(validate_candidate(FIXED, ORIGINAL), FIXED)
        self.assertEqual(validate_candidate("\n" + FIXED + "\n", ORIGINAL), FIXED)
        fence = chr(96)*3
        self.assertEqual(validate_candidate(fence + "csharp\n" + FIXED.rstrip("\n")
                                             + "\n" + fence, ORIGINAL), FIXED)
        for wrong in (
            ORIGINAL, FIXED.replace("values.Sum()", "0"),
            FIXED + "Console.WriteLine(\"leak\");",
            "I fixed it:\n" + FIXED, FIXED.replace("int[]", "var"),
            FIXED.replace("Total:", "Count:"), FIXED + "\0"
        ):
            with self.subTest(wrong=wrong[:40]), self.assertRaises(ValueError):
                validate_candidate(wrong, ORIGINAL)

    def test_pinned_open_code_request_filter(self):
        self.assertTrue(validate_chat_request(request_data(), MODEL, PROMPT))
        self.assertFalse(validate_chat_request(request_data("other:1"), MODEL, PROMPT))
        self.assertFalse(validate_chat_request(request_data(prompt="other"), MODEL, PROMPT))
        self.assertFalse(validate_chat_request(b'{"model":"qwen3.5:4b","model":"qwen3.5:4b"}', MODEL, PROMPT))
        altered = json.loads(request_data())
        altered["messages"].append({"role": "user", "content": PROMPT})
        self.assertFalse(validate_chat_request(json.dumps(altered).encode(), MODEL, PROMPT))
        altered = json.loads(request_data())
        altered["stream"] = 1
        self.assertFalse(validate_chat_request(json.dumps(altered).encode(), MODEL, PROMPT))

    def test_stream_fail_closed_on_tools_or_truncation(self):
        self.assertEqual(parse_proposal_sse(events(), MODEL), FIXED)
        for broken in (
            events(model="other:1"), events(tool=True),
            events(finish="tool_calls"), events()[:-16],
            events(content="x"*5000), b"not data\n"+events(),
        ):
            with self.subTest(broken=broken[:24]), self.assertRaises(ValueError):
                parse_proposal_sse(broken, MODEL)

    def test_real_upstream_rebuilt_without_opencode_tools(self):
        with mock.patch("pinned_repair_proposal.verify_model_digest",
                        return_value=True) as pin, \
             mock.patch("pinned_repair_proposal.http.client.HTTPConnection") as server:
            reply = server.return_value.getresponse.return_value
            reply.status = 200
            reply.getheader.return_value = "text/event-stream"
            reply.read.return_value = events()
            candidate = request_repair_proposal(MODEL, DIGEST, PROMPT, ORIGINAL)
            self.assertEqual(candidate, FIXED)
            self.assertEqual(pin.call_count, 2)
            server.assert_called_once_with("127.0.0.1", 11434, timeout=240)
            sent = server.return_value.request.call_args
            self.assertEqual(sent.args[:2], ("POST", "/v1/chat/completions"))
            body = json.loads(sent.kwargs["body"])
            self.assertEqual(body["model"], MODEL)
            self.assertTrue(body["stream"])
            self.assertEqual(body["messages"][-1]["content"], PROMPT)
            self.assertNotIn("tools", body)
            self.assertNotIn("OpenCode internals not forwarded", json.dumps(body))


@unittest.skipUnless(os.name == "posix" and hasattr(socket, "AF_UNIX"), "Unix sockets required")
class RepairBrokerTests(unittest.TestCase):
    def test_one_attempt_foreign_model_denied_and_no_tools(self):
        with tempfile.TemporaryDirectory() as temp:
            private = pathlib.Path(temp) / "private"
            private.mkdir(mode=0o700)
            sock = private / "broker.sock"
            state = {"attempts": 0, "completed": 0, "candidate": None}
            with mock.patch("pinned_repair_proposal.request_repair_proposal",
                            return_value=FIXED) as infer, \
                 serve_bridge(sock, pinned_repair_handler(MODEL, DIGEST, state)):
                def send(payload):
                    conn = http.client.HTTPConnection("localhost")
                    def connect():
                        conn.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                        conn.sock.connect(str(sock))
                    conn.connect = connect
                    try:
                        conn.request("POST", "/v1/chat/completions",
                            body=payload, headers={"Content-Type": "application/json"})
                        response = conn.getresponse()
                        status, content = response.status, response.read()
                        return status, content
                    finally:
                        conn.close()
                self.assertEqual(send(request_data(model="other:1"))[0], 403)
                status, content = send(request_data())
                self.assertEqual(status, 200)
                self.assertIn(b"values.Sum()", content)
                self.assertNotIn(b"tool_calls", content)
                self.assertEqual(send(request_data())[0], 403)
            self.assertEqual(state, {"attempts": 1, "completed": 1, "candidate": FIXED})
            infer.assert_called_once()
