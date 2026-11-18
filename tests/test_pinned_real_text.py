"""Model-free hostile inputs and fixed-provider contract for real OpenCode text."""
import http.client
import io
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
from pinned_real_text import (
    TASK, MAX_REQUEST, parse_text_sse, validate_open_code_chat,
    request_real_text, pinned_real_text_handler,
)
from ollama_bridge import serve_bridge

MODEL, DIGEST = "qwen3.5:4b", "d"*64


def request(user=TASK):
    return {"model": MODEL, "stream": True,
            "messages": [
                {"role": "system", "content": "Untrusted OpenCode system info"},
                {"role": "user", "content": [{"type": "text", "text": user}]},
            ],
            "tools": [{"type": "function", "function": {"name": "shell"}}]}


def encoded(request_data=None):
    return json.dumps(request() if request_data is None else request_data).encode()


def sse(text="READY", model=MODEL, tool=False):
    data = [
        {"model": model, "choices": [{"index": 0,
            "delta": {"role": "assistant"}, "finish_reason": None}]},
        {"model": model, "choices": [{"index": 0,
            "delta": ({"tool_calls": [{"index": 0, "function": {
                "name": "shell", "arguments": "{}"}}]} if tool
                      else {"content": text}), "finish_reason": None}]},
        {"model": model, "choices": [{"index": 0,
            "delta": {}, "finish_reason": "stop"}]},
    ]
    return b"".join(b"data: "+json.dumps(d).encode()+b"\n\n" for d in data)+b"data: [DONE]\n\n"


class RealTextTests(unittest.TestCase):
    def test_open_code_request_is_bounded_and_pinned(self):
        self.assertTrue(validate_open_code_chat(encoded(), MODEL))
        self.assertFalse(validate_open_code_chat(b"{}", MODEL))
        self.assertFalse(validate_open_code_chat(b"x"*(MAX_REQUEST+1), MODEL))
        self.assertFalse(validate_open_code_chat(
            b'{"model":"qwen3.5:4b","model":"qwen3.5:4b"}', MODEL))
        self.assertFalse(validate_open_code_chat(
            encoded({**request(), "model": "different:1"}), MODEL))
        self.assertFalse(validate_open_code_chat(
            encoded({**request(), "stream": False}), MODEL))
        self.assertFalse(validate_open_code_chat(
            encoded({**request(), "stream": 1}), MODEL))
        self.assertFalse(validate_open_code_chat(
            encoded({**request(), "messages": [
                {"role": "user", "content": TASK}, {"role": "user", "content": "other"}]}),
            MODEL))
        self.assertFalse(validate_open_code_chat(
            encoded({**request(), "messages": [{"role": "tool", "content": TASK}]}), MODEL))
        self.assertFalse(validate_open_code_chat(
            encoded({**request(), "messages": [{"role": "user", "content": "Other task"}]}),
            MODEL))
        self.assertFalse(validate_open_code_chat(
            encoded({**request(), "messages": [{"role": "user",
                "content": TASK + TASK}]}), MODEL))

    def test_strict_text_sse(self):
        self.assertEqual(parse_text_sse(sse("READY"), MODEL), "READY")
        self.assertEqual(parse_text_sse(sse("The answer is READY."), MODEL),
                         "The answer is READY.")
        for bad in (sse("NOTHING"), sse(model="foreign:1"), sse(tool=True),
                    sse().replace(b"data: [DONE]", b""),
                    sse().replace(b'"stop"', b'"tool_calls"'),
                    b"NOPE\n"+sse(), b"data: "+b"x"*18000+b"\n"):
            with self.subTest(bad=bad[:40]), self.assertRaises(ValueError):
                parse_text_sse(bad, MODEL)

    def test_upstream_is_reconstructed_and_has_no_tools(self):
        with mock.patch("pinned_real_text.verify_model_digest", return_value=True) as pin, \
             mock.patch("pinned_real_text.http.client.HTTPConnection") as conn:
            reply = conn.return_value.getresponse.return_value
            reply.status = 200
            reply.getheader.return_value = "text/event-stream"
            reply.read.return_value = sse()
            result = request_real_text(MODEL, DIGEST)
            self.assertIn(b'READY', result)
            self.assertNotIn(b'"tool_calls"', result)
            self.assertEqual(pin.call_count, 2)
            conn.assert_called_once_with("127.0.0.1", 11434, timeout=240)
            sent = conn.return_value.request.call_args
            self.assertEqual(sent.args[:2], ("POST", "/v1/chat/completions"))
            body = json.loads(sent.kwargs["body"])
            self.assertEqual(body["model"], MODEL)
            self.assertEqual(body["messages"][-1]["content"], TASK)
            self.assertTrue(body["stream"])
            self.assertNotIn("tools", body)
            self.assertNotIn("Untrusted OpenCode system info", json.dumps(body))

    def test_model_drift_or_tools_fail_closed(self):
        for stream, pin in ((sse(tool=True), [True, True]),
                            (sse(), [True, False])):
            with self.subTest(stream=stream[:20], pin=pin), \
                 mock.patch("pinned_real_text.verify_model_digest", side_effect=pin), \
                 mock.patch("pinned_real_text.http.client.HTTPConnection") as conn, \
                 self.assertRaises(ValueError):
                reply = conn.return_value.getresponse.return_value
                reply.status = 200
                reply.getheader.return_value = "text/event-stream"
                reply.read.return_value = stream
                request_real_text(MODEL, DIGEST)


@unittest.skipUnless(os.name == "posix" and hasattr(socket, "AF_UNIX"),
                     "Unix domain socket required")
class RealTextUnixTests(unittest.TestCase):
    def test_one_request_only_and_foreign_model_denied(self):
        with tempfile.TemporaryDirectory() as temp:
            private = pathlib.Path(temp) / "private"
            private.mkdir(mode=0o700)
            sk = private / "ollama.sock"
            state = {"attempts": 0, "completed": 0}
            with mock.patch("pinned_real_text.request_real_text", return_value=sse()), \
                 mock.patch("pinned_openai.verify_model_digest", return_value=True), \
                 serve_bridge(sk, pinned_real_text_handler(MODEL, DIGEST, state)):
                def send(body):
                    conn = http.client.HTTPConnection("localhost")
                    def connect():
                        conn.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                        conn.sock.connect(str(sk))
                    conn.connect = connect
                    try:
                        conn.request("POST", "/v1/chat/completions", body=body,
                                     headers={"Content-Type": "application/json"})
                        reply = conn.getresponse()
                        status = reply.status
                        reply.read()
                        return status
                    finally:
                        conn.close()
                other = {**request(), "model": "foreign:1"}
                self.assertEqual(send(encoded(other)), 403)
                self.assertEqual(send(encoded()), 200)
                self.assertEqual(send(encoded()), 403)
            self.assertEqual(state, {"attempts": 1, "completed": 1})


if __name__ == "__main__":
    unittest.main()
