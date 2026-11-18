"""M0.6 bounded, model-pinned Ollama chat over the existing private Unix bridge.

This is a one-prompt feasibility contract, NOT an OpenCode-compatible provider.
Only the fixed probe prompt is accepted. No streaming, tools or arbitrary prompts.
"""
from __future__ import annotations

import http.client
import http.server
import json
from typing import Any

from ollama_bridge import BridgeHandler, fetch_local_ollama

PROBE_PROMPT = "Reply with the single word READY."
MAX_REQUEST_BYTES = 2048
MAX_RESPONSE_BYTES = 262144


def validate_probe_request(data: bytes, model: str) -> bool:
    if not 0 < len(data) <= MAX_REQUEST_BYTES:
        return False
    try:
        request = json.loads(data)
    except (UnicodeError, ValueError):
        return False
    return (isinstance(request, dict)
            and set(request) == {"model", "messages", "stream"}
            and request["model"] == model
            and request["stream"] is False
            and request["messages"] == [{"role": "user", "content": PROBE_PROMPT}])


def verify_model_digest(model: str, digest: str) -> bool:
    """Check the Ollama tag before/after inference; this is a coarse race check."""
    tags = json.loads(fetch_local_ollama("/api/tags"))
    return any(item.get("name") == model and item.get("digest") == digest
               for item in tags.get("models", []))


def request_pinned_completion(model: str, digest: str) -> bytes:
    if not verify_model_digest(model, digest):
        raise ValueError("Pinned model digest changed or is missing")
    # Reconstruct every upstream field. Never forward an untrusted JSON object.
    request: dict[str, Any] = {
        "model": model,
        "messages": [{"role": "user", "content": PROBE_PROMPT}],
        "stream": False,
        "think": False,
        "options": {"temperature": 0, "num_predict": 64},
    }
    body = json.dumps(request).encode("utf-8")
    conn = http.client.HTTPConnection("127.0.0.1", 11434, timeout=240)
    try:
        conn.request("POST", "/api/chat", body=body,
                     headers={"Content-Type": "application/json", "Connection": "close"})
        response = conn.getresponse()
        if response.status != 200:
            raise RuntimeError(f"Ollama returned HTTP {response.status}")
        result = response.read(MAX_RESPONSE_BYTES + 1)
        if len(result) > MAX_RESPONSE_BYTES:
            raise ValueError("Ollama response exceeded size limit")
    finally:
        conn.close()
    if not verify_model_digest(model, digest):
        raise ValueError("Pinned model digest changed while running")
    decoded = json.loads(result)
    message = decoded.get("message", {})
    if (decoded.get("model") != model or decoded.get("done") is not True
            or not isinstance(message, dict)
            or message.get("role") != "assistant"
            or not isinstance(message.get("content"), str)
            or not message["content"].strip()):
        raise ValueError("Ollama returned incomplete/mismatched model output")
    # Do not relay unbounded raw provider data or hidden reasoning.
    sanitized = {"model": model, "done": True,
                 "message": {"role": "assistant", "content": message["content"][:2048]}}
    return json.dumps(sanitized, separators=(",", ":")).encode("utf-8")


def pinned_handler(model: str, digest: str) -> type[BridgeHandler]:
    if not model or not digest or "/" in model:
        raise ValueError("Missing or malformed model pin")

    class Handler(BridgeHandler):
        def do_POST(self) -> None:
            if self.path != "/api/chat":
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
            payload = self.rfile.read(int(length))
            if not validate_probe_request(payload, model):
                self._reply(403, b'{"error":"request does not match pinned probe"}')
                return
            try:
                result = request_pinned_completion(model, digest)
            except (OSError, RuntimeError, ValueError, json.JSONDecodeError):
                self._reply(502, b'{"error":"pinned local inference unavailable"}')
                return
            self._reply(200, result)

    return Handler
