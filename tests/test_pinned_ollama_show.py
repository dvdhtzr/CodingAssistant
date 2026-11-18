"""Bounded POST /api/show contract: native OpenCode discovery, no inference."""
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
from pinned_openai import exact_show_request, pinned_show_response, pinned_openai_handler
from ollama_bridge import serve_bridge

MODEL, DIGEST = "qwen3.5:4b", "d" * 64


def fixture():
    return {
        "capabilities": ["completion", "tools", "thinking"],
        "details": {
            "format": "gguf", "family": "qwen3.5", "families": ["qwen3.5"],
            "parameter_size": "4B", "quantization_level": "Q4_K_M",
            "host_only_private": "DO-NOT-DISCLOSE",
        },
        "model_info": {
            "qwen3.5.context_length": 32768,
            "qwen3.5.embedding_length": 4096,
            "host.path": "/home/secret",
        },
        "modelfile": "FROM /home/owner/.ollama/private-sha",
        "template": "SECRET_SYSTEM_PROMPT",
        "license": "Private license text",
    }


class NativeShowTests(unittest.TestCase):
    def test_only_exact_show_request_is_accepted(self):
        self.assertTrue(exact_show_request(json.dumps({"model": MODEL}).encode(), MODEL))
        for payload in (b"{}", b'{"model":"other:4b"}',
                        b'{"model":"qwen3.5:4b","verbose":true}',
                        b'{"model":"qwen3.5:4b","model":"qwen3.5:4b"}',
                        b'{"model":42}', b"", b"x"*257):
            with self.subTest(payload=payload[:50]):
                self.assertFalse(exact_show_request(payload, MODEL))

    def test_only_safe_capabilities_and_context_are_disclosed(self):
        with mock.patch("pinned_openai.verify_model_digest", return_value=True) as verified, \
             mock.patch("pinned_openai.ollama_json", return_value=fixture()) as remote:
            safe = pinned_show_response(MODEL, DIGEST)
        self.assertEqual(verified.call_count, 2)
        remote.assert_called_once_with("http://127.0.0.1:11434", "/api/show",
                                       {"model": MODEL}, timeout=12)
        self.assertEqual(json.loads(safe)["capabilities"],
                         ["completion", "tools", "thinking"])
        self.assertEqual(json.loads(safe)["model_info"], {"qwen3.5.context_length": 32768})
        for leaked in (b"SECRET_SYSTEM_PROMPT", b"/home/owner", b"Private license",
                       b"host_only_private", b"embedding_length", b"/home/secret"):
            self.assertNotIn(leaked, safe)

    def test_capabilities_missing_or_invalid_must_fail_closed(self):
        for caps in ([], ["embedding"], ["tools"], ["completion"], "completion", [42]):
            data = fixture()
            data["capabilities"] = caps
            with self.subTest(caps=caps), \
                 mock.patch("pinned_openai.verify_model_digest", return_value=True), \
                 mock.patch("pinned_openai.ollama_json", return_value=data), \
                 self.assertRaises(ValueError):
                pinned_show_response(MODEL, DIGEST)

    def test_digest_changed_must_fail_closed(self):
        with mock.patch("pinned_openai.verify_model_digest", side_effect=[True, False]), \
             mock.patch("pinned_openai.ollama_json", return_value=fixture()), \
             self.assertRaises(ValueError):
            pinned_show_response(MODEL, DIGEST)


@unittest.skipUnless(os.name == "posix" and hasattr(socket, "AF_UNIX"), "AF_UNIX required")
class UnixShowTests(unittest.TestCase):
    def request(self, body, path="/api/show"):
        with tempfile.TemporaryDirectory() as temp:
            private = pathlib.Path(temp) / "private"
            private.mkdir(mode=0o700)
            socket_file = private / "ollama.sock"
            with mock.patch("pinned_openai.verify_model_digest", return_value=True), \
                 mock.patch("pinned_openai.ollama_json", return_value=fixture()) as upstream, \
                 mock.patch("pinned_openai.request_pinned_stream") as inference:
                with serve_bridge(socket_file, pinned_openai_handler(MODEL, DIGEST)):
                    conn = http.client.HTTPConnection("localhost")
                    def connect():
                        conn.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                        conn.sock.connect(str(socket_file))
                    conn.connect = connect
                    try:
                        conn.request("POST", path, body=body,
                                     headers={"Content-Type": "application/json"})
                        reply = conn.getresponse()
                        status, raw = reply.status, reply.read()
                    finally:
                        conn.close()
                return status, raw, upstream.call_count, inference.call_count

    def test_selected_model_only_and_no_inference(self):
        status, raw, count, inference = self.request(b'{"model":"qwen3.5:4b"}')
        self.assertEqual((status, count, inference), (200, 1, 0))
        self.assertIn("completion", json.loads(raw)["capabilities"])

    def test_wrong_show_model_denied_without_upstream(self):
        for body in (b'{"model":"foreign:1"}', b'{"model":"qwen3.5:4b","other":true}',
                     b'{"model":"qwen3.5:4b","model":"qwen3.5:4b"}'):
            status, _, count, inference = self.request(body)
            self.assertEqual((status, count, inference), (403, 0, 0))

    def test_query_string_does_not_bypass_exact_path(self):
        status, _, count, inference = self.request(b'{"model":"qwen3.5:4b"}',
                                                   "/api/show?verbose=true")
        self.assertEqual((status, count, inference), (403, 0, 0))


if __name__ == "__main__":
    unittest.main()
