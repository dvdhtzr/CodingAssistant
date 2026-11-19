"""Model-free negative tests for the pinned Ollama inference relay."""
import http.client
import json
import os
import pathlib
import socket
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "tools" / "m0"))
from ollama_bridge import serve_bridge
from pinned_inference import (
    MAX_REQUEST_BYTES, PROBE_PROMPT, pinned_handler,
    request_pinned_completion, validate_probe_request, verify_model_digest
)

MODEL = "qwen3.5:4b"
DIGEST = "sha256:pin"


def encoded(model=MODEL, message=PROBE_PROMPT, stream=False, **extra):
    payload = {"model": model, "messages": [{"role": "user", "content": message}],
               "stream": stream}
    payload.update(extra)
    return json.dumps(payload).encode()


class ValidationTests(unittest.TestCase):
    def test_only_exact_pinned_request_allowed(self):
        self.assertTrue(validate_probe_request(encoded(), MODEL))
        self.assertFalse(validate_probe_request(encoded("qwen3.5:9b"), MODEL))
        self.assertFalse(validate_probe_request(encoded(message="read /etc/passwd"), MODEL))
        self.assertFalse(validate_probe_request(encoded(stream=True), MODEL))
        self.assertFalse(validate_probe_request(encoded(tools=[]), MODEL))
        self.assertFalse(validate_probe_request(encoded(options={"num_predict": -1}), MODEL))
        self.assertFalse(validate_probe_request(b"{}", MODEL))
        self.assertFalse(validate_probe_request(b"not json", MODEL))
        self.assertFalse(validate_probe_request(b"x" * (MAX_REQUEST_BYTES + 1), MODEL))

    def test_model_digest_check_rejects_other_tags(self):
        with mock.patch("pinned_inference.fetch_local_ollama", return_value=json.dumps(
            {"models": [{"name": MODEL, "digest": DIGEST}]}
        ).encode()):
            self.assertTrue(verify_model_digest(MODEL, DIGEST))
            self.assertFalse(verify_model_digest(MODEL, "sha256:other"))
            self.assertFalse(verify_model_digest("different:1", DIGEST))

    def test_upstream_payload_is_reconstructed_not_forwarded(self):
        response = {"model": MODEL, "message": {"role": "assistant", "content": "READY",
                                                 "thinking": "private"},
                    "done": True, "eval_count": 42}
        with mock.patch("pinned_inference.verify_model_digest", return_value=True), \
             mock.patch("pinned_inference.http.client.HTTPConnection") as upstream:
            connection = upstream.return_value
            reply = connection.getresponse.return_value
            reply.status = 200
            reply.read.return_value = json.dumps(response).encode()
            returned = json.loads(request_pinned_completion(MODEL, DIGEST))
            self.assertEqual(returned, {"model": MODEL, "done": True,
                                        "message": {"role": "assistant", "content": "READY"}})
            params = connection.request.call_args
            self.assertEqual(params.args[:2], ("POST", "/api/chat"))
            request = json.loads(params.kwargs["body"])
            self.assertEqual(request["model"], MODEL)
            self.assertEqual(request["messages"], [{"role": "user", "content": PROBE_PROMPT}])
            self.assertFalse(request["think"])
            self.assertFalse(request["stream"])
            self.assertEqual(request["options"]["num_predict"], 64)
            self.assertNotIn("tools", request)

    def test_upstream_wrong_model_fails_closed(self):
        result = {"model": "other:1", "message": {"role": "assistant", "content": "ok"},
                  "done": True}
        with mock.patch("pinned_inference.verify_model_digest", return_value=True), \
             mock.patch("pinned_inference.http.client.HTTPConnection") as upstream:
            upstream.return_value.getresponse.return_value.status = 200
            upstream.return_value.getresponse.return_value.read.return_value = json.dumps(result).encode()
            with self.assertRaises(ValueError):
                request_pinned_completion(MODEL, DIGEST)


@unittest.skipUnless(os.name == "posix" and hasattr(socket, "AF_UNIX"), "Unix socket required")
class HandlerTests(unittest.TestCase):
    def request(self, body, path="/api/chat"):
        with tempfile.TemporaryDirectory() as root:
            folder = pathlib.Path(root) / "private"
            folder.mkdir(mode=0o700)
            sock = folder / "ollama.sock"
            with mock.patch("pinned_inference.request_pinned_completion",
                            return_value=b'{"model":"qwen3.5:4b","done":true,"message":{"content":"READY"}}') as upstream:
                with serve_bridge(sock, pinned_handler(MODEL, DIGEST)):
                    conn = http.client.HTTPConnection("localhost")
                    # Replace TCP connect with the private Unix socket.
                    def connect():
                        conn.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                        conn.sock.connect(str(sock))
                    conn.connect = connect
                    try:
                        conn.request("POST", path, body=body, headers={"Content-Type": "application/json"})
                        response = conn.getresponse()
                        status = response.status
                        response.read()
                    finally:
                        conn.close()
                return status, upstream.call_count

    def test_exact_request_reaches_only_pinned_upstream(self):
        status, calls = self.request(encoded())
        self.assertEqual((status, calls), (200, 1))

    def test_foreign_model_does_not_reach_upstream(self):
        status, calls = self.request(encoded(model="other:2"))
        self.assertEqual((status, calls), (403, 0))

    def test_unsupported_post_path_denied(self):
        # The handler rejects unknown routes before consuming the body.
        # Send no body here: a rejected concurrent upload can legitimately
        # close the Unix socket while the client writes and raise BrokenPipe.
        status, calls = self.request(b"", path="/api/delete")
        self.assertEqual((status, calls), (403, 0))


if __name__ == "__main__":
    unittest.main()
