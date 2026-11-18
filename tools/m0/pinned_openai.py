"""M0.8b-1: bounded OpenAI wire relay over a private Unix socket.

Only one literal Ollama model, fixed prompt, and inert report_ready function.
This is NOT a general inference proxy and never starts OpenCode or runs tools.
"""
from __future__ import annotations

import http.client
import io
import json

from ollama_bridge import BridgeHandler
from openai_wire_probe import MAX_BYTES, payload, read_sse
from pinned_inference import verify_model_digest

MAX_REQUEST_BYTES = 4096


def _reject_duplicates(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON key")
        result[key] = value
    return result


def _same_types_and_values(actual: object, expected: object) -> bool:
    if type(actual) is not type(expected):
        return False
    if isinstance(expected, dict):
        return (actual.keys() == expected.keys()
                and all(_same_types_and_values(actual[k], expected[k]) for k in expected))
    if isinstance(expected, list):
        return (len(actual) == len(expected)
                and all(_same_types_and_values(a, b) for a, b in zip(actual, expected)))
    return actual == expected


def exact_request(data: bytes, model: str) -> bool:
    if not 0 < len(data) <= MAX_REQUEST_BYTES:
        return False
    try:
        parsed = json.loads(data, object_pairs_hook=_reject_duplicates)
        return _same_types_and_values(parsed, payload(model))
    except (UnicodeError, ValueError):
        return False


def request_pinned_stream(model: str, digest: str) -> bytes:
    """Validate provider SSE before emitting a new small, deterministic stream."""
    if not verify_model_digest(model, digest):
        raise ValueError("Pinned Ollama model changed before inference")
    connection = http.client.HTTPConnection("127.0.0.1", 11434, timeout=300)
    try:
        body = json.dumps(payload(model), separators=(",", ":")).encode("utf-8")
        connection.request("POST", "/v1/chat/completions", body=body,
                           headers={"Content-Type": "application/json",
                                    "Connection": "close"})
        response = connection.getresponse()
        if response.status != 200:
            raise RuntimeError("Local Ollama inference returned non-200")
        if "text/event-stream" not in response.getheader("Content-Type", ""):
            raise ValueError("Local Ollama did not return SSE")
        raw = response.read(MAX_BYTES + 1)
        if len(raw) > MAX_BYTES:
            raise ValueError("Local Ollama SSE over budget")
    finally:
        connection.close()
    # The model's output is NEVER executed or sent through to the caller.
    read_sse(io.BytesIO(raw), model)
    if not verify_model_digest(model, digest):
        raise ValueError("Pinned Ollama model changed after inference")
    chunks = [
        {"model": model, "choices": [{"index": 0,
                                     "delta": {"role": "assistant"},
                                     "finish_reason": None}]},
        {"model": model, "choices": [{"index": 0,
            "delta": {"tool_calls": [{"index": 0, "id": "call-m08b-ready",
                     "type": "function", "function": {
                         "name": "report_ready",
                         "arguments": '{"status":"READY"}'}}]},
            "finish_reason": None}]},
        {"model": model, "choices": [{"index": 0, "delta": {},
                                     "finish_reason": "tool_calls"}]},
    ]
    return (b"".join(b"data: " + json.dumps(chunk, separators=(",", ":")).encode()
                     + b"\n\n" for chunk in chunks) + b"data: [DONE]\n\n")


def pinned_openai_handler(model: str, digest: str) -> type[BridgeHandler]:
    if not model or not digest or "/" in model:
        raise ValueError("Missing model/digest pin")

    class Handler(BridgeHandler):
        def do_GET(self) -> None:
            if self.path != "/v1/models":
                super().do_GET()
                return
            if self.headers.get("Content-Length") or self.headers.get("Transfer-Encoding"):
                self._reply(403, b'{"error":"not permitted"}')
                return
            if not verify_model_digest(model, digest):
                self._reply(502, b'{"error":"pinned local model unavailable"}')
                return
            public = {"object": "list", "data": [
                {"id": model, "object": "model", "owned_by": "ollama"}]}
            self._reply(200, json.dumps(public, separators=(",", ":")).encode())

        def do_POST(self) -> None:
            if self.path != "/v1/chat/completions":
                self._reply(403, b'{"error":"not permitted"}')
                return
            length = self.headers.get("Content-Length")
            content_type = self.headers.get("Content-Type", "").split(";")[0].strip().lower()
            if (not length or not length.isascii() or not length.isdecimal()
                    or not 0 < int(length) <= MAX_REQUEST_BYTES
                    or content_type != "application/json"
                    or self.headers.get("Transfer-Encoding")):
                self._reply(403, b'{"error":"invalid request framing"}')
                return
            data = self.rfile.read(int(length))
            if not exact_request(data, model):
                self._reply(403, b'{"error":"request does not match pinned probe"}')
                return
            try:
                answer = request_pinned_stream(model, digest)
            except (OSError, RuntimeError, ValueError, TypeError, http.client.HTTPException):
                self._reply(502, b'{"error":"pinned local inference unavailable"}')
                return
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Content-Length", str(len(answer)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(answer)
            self.close_connection = True

    return Handler
