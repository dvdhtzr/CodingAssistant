"""M0.8b-1 fixed Unix/OpenAI relay contracts, with no real Ollama/model use."""
import http.client
import io
import json
import pathlib
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))

from ollama_bridge import serve_bridge
from openai_wire_probe import payload
from pinned_openai import (exact_request, pinned_openai_handler,
                           request_pinned_stream, MAX_REQUEST_BYTES)
from isolated_openai_probe import run_command, assess, SCRIPT

MODEL = "qwen3.5:4b"
DIGEST = "sha256:fixed-test"


def sample():
    chunks = [
        {"model": MODEL, "choices": [{"index": 0, "delta": {"role": "assistant"}, "finish_reason": None}]},
        {"model": MODEL, "choices": [{"index": 0, "delta": {"tool_calls": [
            {"index": 0, "id": "call-test", "type": "function",
             "function": {"name": "report_ready", "arguments": '{"status":"READY"}'}}
        ]}, "finish_reason": None}]},
        {"model": MODEL, "choices": [{"index": 0, "delta": {}, "finish_reason": "tool_calls"}]},
    ]
    return b"".join(b"data: " + json.dumps(c).encode() + b"\n\n" for c in chunks) + b"data: [DONE]\n\n"


class RequestTests(unittest.TestCase):
    def test_request_is_exactly_one_inert_function_and_model(self):
        valid = json.dumps(payload(MODEL)).encode()
        self.assertTrue(exact_request(valid, MODEL))
        wrong = payload(MODEL)
        wrong["model"] = "other:model"
        self.assertFalse(exact_request(json.dumps(wrong).encode(), MODEL))
        changed = payload(MODEL)
        changed["tools"][0]["function"]["name"] = "shell"
        self.assertFalse(exact_request(json.dumps(changed).encode(), MODEL))
        changed = payload(MODEL)
        changed["messages"] = [{"role": "user", "content": "read secret"}]
        self.assertFalse(exact_request(json.dumps(changed).encode(), MODEL))
        self.assertFalse(exact_request(b'{"model":"qwen3.5:4b","model":"qwen3.5:4b"}', MODEL))
        numeric = payload(MODEL)
        numeric["stream"] = 1  # Python bool==int must not weaken strict schema.
        self.assertFalse(exact_request(json.dumps(numeric).encode(), MODEL))
        self.assertFalse(exact_request(b"{}", MODEL))
        self.assertFalse(exact_request(b"x" * (MAX_REQUEST_BYTES + 1), MODEL))

    def test_upstream_called_once_and_output_sanitized(self):
        with mock.patch("pinned_openai.verify_model_digest", return_value=True) as pinned, \
             mock.patch("pinned_openai.http.client.HTTPConnection") as upstream:
            answer = upstream.return_value.getresponse.return_value
            answer.status = 200
            answer.getheader.return_value = "text/event-stream"
            answer.read.return_value = sample()
            output = request_pinned_stream(MODEL, DIGEST)
            self.assertIn(b"data: [DONE]", output)
            self.assertIn(b"report_ready", output)
            self.assertNotIn(b"call-test", output)
            self.assertEqual(pinned.call_count, 2)
            upstream.assert_called_once_with("127.0.0.1", 11434, timeout=300)
            args = upstream.return_value.request.call_args
            self.assertEqual(args.args[:2], ("POST", "/v1/chat/completions"))
            self.assertEqual(json.loads(args.kwargs["body"]), payload(MODEL))

    def test_invalid_stream_fails_without_sending_model_output(self):
        with mock.patch("pinned_openai.verify_model_digest", return_value=True), \
             mock.patch("pinned_openai.http.client.HTTPConnection") as upstream:
            answer = upstream.return_value.getresponse.return_value
            answer.status = 200
            answer.getheader.return_value = "text/event-stream"
            answer.read.return_value = sample().replace(b"report_ready", b"wrong_tool")
            with self.assertRaises(ValueError):
                request_pinned_stream(MODEL, DIGEST)

    def test_command_keeps_isolation_and_immutable_image(self):
        with tempfile.TemporaryDirectory() as root:
            p = pathlib.Path(root)
            private = p / "private"
            private.mkdir()
            sentinel = p / "host"
            sentinel.write_text("hidden")
            cmd = run_command("a" * 64, 1000, 1000, private, MODEL, sentinel)
        for flag in ("--network=none", "--read-only", "--cap-drop=all",
                     "--security-opt=no-new-privileges", "--pull=never",
                     "--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup",
                     "--userns=keep-id", "--entrypoint=node"):
            self.assertIn(flag, cmd)
        self.assertEqual(cmd.count("--volume"), 1)
        self.assertIn(":/run/codingassistant:ro", cmd[cmd.index("--volume") + 1])
        self.assertNotIn("--network=host", cmd)
        self.assertNotIn("--privileged", cmd)
        self.assertNotIn("--volume=/home", " ".join(cmd))
        self.assertEqual(cmd[cmd.index("--entrypoint=node") + 1], "a" * 64)
        self.assertIn("--eval", cmd)
        with tempfile.TemporaryDirectory() as root:
            p = pathlib.Path(root)
            private = p / "private"
            private.mkdir()
            sentinel = p / "host"
            sentinel.write_text("hidden")
            with self.assertRaises(ValueError):
                run_command("mutable:tag", 1000, 1000, private, MODEL, sentinel)

    def test_every_required_check_must_be_true(self):
        all_ok = {key: True for key in (
            "unprivileged", "root_read_only", "host_sentinel_hidden",
            "outbound_tcp_denied", "metadata_pinned", "wrong_model_denied",
            "unsupported_path_denied", "sse_tool_call")}
        self.assertTrue(assess({"checks": all_ok}))
        for key in all_ok:
            altered = dict(all_ok)
            altered[key] = False
            self.assertFalse(assess({"checks": altered}), key)
        self.assertFalse(assess({"checks": {}}))
        self.assertFalse(assess({"checks": {**all_ok, "extra": True}}))

    def test_client_contains_no_process_or_agent_execution(self):
        text = SCRIPT.read_text()
        self.assertNotIn("child_process", text)
        self.assertNotIn("opencode run", text)
        self.assertIn('server.listen(0, "127.0.0.1"', text)
        self.assertIn('"/proc/self/mountinfo"', text)

    @unittest.skipUnless(shutil.which("node"), "Node not installed on test host")
    def test_node_client_syntax(self):
        result = subprocess.run(["node", "--check", str(SCRIPT)],
                                capture_output=True, text=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stderr)


@unittest.skipUnless(os.name == "posix" and hasattr(socket, "AF_UNIX"),
                     "Unix domain socket required")
class UnixHandlerTests(unittest.TestCase):
    def request(self, method, path, body=None):
        with tempfile.TemporaryDirectory() as tmp:
            folder = pathlib.Path(tmp) / "private"
            folder.mkdir(mode=0o700)
            sock = folder / "ollama.sock"
            with mock.patch("pinned_openai.verify_model_digest", return_value=True), \
                 mock.patch("pinned_openai.request_pinned_stream",
                            return_value=sample()) as upstream:
                with serve_bridge(sock, pinned_openai_handler(MODEL, DIGEST)):
                    connection = http.client.HTTPConnection("localhost")
                    def connect():
                        connection.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                        connection.sock.connect(str(sock))
                    connection.connect = connect
                    try:
                        headers = {"Content-Type": "application/json"} if body else {}
                        connection.request(method, path, body=body, headers=headers)
                        response = connection.getresponse()
                        status, media_type = response.status, response.getheader("Content-Type")
                        response.read()
                    finally:
                        connection.close()
                return status, media_type, upstream.call_count

    def test_literal_prompt_only(self):
        good = json.dumps(payload(MODEL)).encode()
        code, type_, count = self.request("POST", "/v1/chat/completions", good)
        self.assertEqual((code, count), (200, 1))
        self.assertEqual(type_, "text/event-stream")
        bad = payload(MODEL)
        bad["model"] = "other:1"
        self.assertEqual(self.request("POST", "/v1/chat/completions",
                                      json.dumps(bad).encode())[::2], (403, 0))

    def test_foreign_methods_paths_denied(self):
        self.assertEqual(self.request("POST", "/api/delete", b"{}")[0], 403)
        self.assertEqual(self.request("GET", "/v1/chat/completions")[0], 403)
        self.assertEqual(self.request("GET", "/v1/models?unexpected=true")[0], 403)
        self.assertEqual(self.request("GET", "/v1/models")[0], 200)


if __name__ == "__main__":
    unittest.main()
