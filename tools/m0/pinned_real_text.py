"""M0.8b-2b: one real, bounded, text-only Qwen response to pinned OpenCode.

The OpenCode process may include its own system messages and tool definitions;
NONE of those reach Ollama. The host reconstructs one fixed prompt, strips
reasoning and all non-content SSE data, and never returns a tool-call delta.
No code, shell commands, or project files are accessible through this broker.
"""
from __future__ import annotations

import http.client
import io
import json
import re
import threading
from typing import Any

from pinned_openai import _reject_duplicates, pinned_openai_handler
from pinned_inference import verify_model_digest
from ollama_bridge import BridgeHandler

TASK = "Reply with the single word READY. Do not use any tools."
MAX_REQUEST = 1024 * 1024
MAX_PROVIDER_SSE = 128 * 1024
MAX_PROVIDER_EVENTS = 1024
MAX_CONTENT = 2048
MAX_MESSAGES = 64
MAX_CALLS = 1


def _user_text(message: dict) -> str:
    content = message.get("content")
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        texts = []
        for part in content:
            if isinstance(part, dict) and isinstance(part.get("text"), str):
                texts.append(part["text"])
        return "\n".join(texts)
    return ""


def validate_open_code_chat(data: bytes, model: str) -> bool:
    """The input must come from a single fixed OpenCode run, never user JSON."""
    if not 0 < len(data) <= MAX_REQUEST:
        return False
    try:
        request = json.loads(data, object_pairs_hook=_reject_duplicates)
    except (ValueError, UnicodeError):
        return False
    if (type(request) is not dict or request.get("model") != model
            or request.get("stream") is not True
            or not isinstance(request.get("messages"), list)
            or not 1 <= len(request["messages"]) <= MAX_MESSAGES):
        return False
    messages = request["messages"]
    if any(not isinstance(msg, dict) or msg.get("role") not in
           ("system", "developer", "user") for msg in messages):
        return False
    user_messages = [m for m in messages if m["role"] == "user"]
    if len(user_messages) != 1:
        return False
    message = _user_text(user_messages[0])
    if message.count(TASK) != 1:
        return False
    # OpenCode can compose its prompt around this one literal instruction,
    # but cannot cause any additional content to reach the provider.
    return True


def parse_text_sse(body: bytes, model: str) -> str:
    """Parse completed Ollama text SSE; disallow any tool call and foreign model."""
    if not 0 < len(body) <= MAX_PROVIDER_SSE:
        raise ValueError("Provider streaming byte budget exceeded")
    content: list[str] = []
    seen_done = False
    seen_finish = None
    events = 0
    for line in io.BytesIO(body):
        if len(line) > 16384:
            raise ValueError("Provider SSE line too large")
        line = line.strip()
        if not line or line.startswith(b":"):
            continue
        if not line.startswith(b"data:"):
            raise ValueError("Unexpected provider event framing")
        raw = line[5:].strip()
        if raw == b"[DONE]":
            seen_done = True
            break
        events += 1
        if events > MAX_PROVIDER_EVENTS:
            raise ValueError("Provider SSE event budget exceeded")
        try:
            event = json.loads(raw)
        except (ValueError, UnicodeError) as exc:
            raise ValueError("Provider emitted invalid JSON") from exc
        if type(event) is not dict or event.get("model") != model:
            raise ValueError("Provider model mismatch")
        choices = event.get("choices")
        if (not isinstance(choices, list) or len(choices) != 1
                or not isinstance(choices[0], dict)
                or choices[0].get("index") != 0):
            raise ValueError("Unexpected provider choice")
        choice = choices[0]
        delta = choice.get("delta")
        if type(delta) is not dict:
            raise ValueError("Missing provider delta")
        if "tool_calls" in delta or "function_call" in delta:
            raise ValueError("Model attempted tool execution")
        if choice.get("finish_reason") is not None:
            seen_finish = choice["finish_reason"]
        fragment = delta.get("content")
        if fragment is not None:
            if not isinstance(fragment, str):
                raise ValueError("Invalid text delta")
            content.append(fragment)
            if sum(map(len, content)) > MAX_CONTENT:
                raise ValueError("Provider content too large")
    if not seen_done or seen_finish != "stop":
        raise ValueError("Provider stream incomplete")
    result = "".join(content).strip()
    if not result or not re.search(r"\bREADY\b", result, flags=re.IGNORECASE):
        raise ValueError("Model did not return the expected word")
    return result


def _completion(model: str, text: str) -> bytes:
    """A normalized OpenAI SSE answer carrying the REAL model text."""
    def chunk(delta: dict, finish: str | None = None) -> bytes:
        obj = {"id": "chatcmpl-m08b-real", "object": "chat.completion.chunk",
               "created": 1, "model": model,
               "choices": [{"index": 0, "delta": delta, "finish_reason": finish}]}
        return b"data: " + json.dumps(obj, separators=(",", ":")).encode() + b"\n\n"
    return (chunk({"role": "assistant"}) + chunk({"content": text}) +
            chunk({}, "stop") + b"data: [DONE]\n\n")


def request_real_text(model: str, digest: str) -> bytes:
    if not verify_model_digest(model, digest):
        raise ValueError("Selected Ollama digest changed before inference")
    # Rebuild the provider request independently. Never send OpenCode's system
    # prompts, tool specs, host paths, or unrecognized model options upstream.
    request = {
        "model": model,
        "messages": [
            {"role": "system", "content": "Return only the word READY. No tools."},
            {"role": "user", "content": TASK},
        ],
        "stream": True,
        "temperature": 0,
        "max_tokens": 256,
    }
    conn = http.client.HTTPConnection("127.0.0.1", 11434, timeout=240)
    try:
        conn.request("POST", "/v1/chat/completions",
                     body=json.dumps(request, separators=(",", ":")).encode(),
                     headers={"Content-Type": "application/json",
                              "Connection": "close"})
        response = conn.getresponse()
        if response.status != 200:
            raise RuntimeError("Local Ollama refused pinned text inference")
        if "text/event-stream" not in response.getheader("Content-Type", ""):
            raise ValueError("Ollama did not return SSE")
        provider = response.read(MAX_PROVIDER_SSE + 1)
        if len(provider) > MAX_PROVIDER_SSE:
            raise ValueError("Provider response larger than bounded maximum")
    finally:
        conn.close()
    actual_text = parse_text_sse(provider, model)
    if not verify_model_digest(model, digest):
        raise ValueError("Selected Ollama digest changed after inference")
    return _completion(model, actual_text)


def pinned_real_text_handler(model: str, digest: str,
                             state: dict[str, int]) -> type[BridgeHandler]:
    """Specialize the proven metadata handler; accept one bounded text inference."""
    base = pinned_openai_handler(model, digest)
    lock = threading.Lock()

    class Handler(base):
        def do_POST(self) -> None:
            if self.path != "/v1/chat/completions":
                return super().do_POST()
            length = self.headers.get("Content-Length", "")
            ctype = self.headers.get("Content-Type", "").split(";")[0].strip().lower()
            if (not length.isascii() or not length.isdecimal()
                    or not 0 < int(length) <= MAX_REQUEST
                    or ctype != "application/json"
                    or self.headers.get("Transfer-Encoding")):
                self._reply(403, b'{"error":"invalid pinned chat framing"}')
                return
            raw = self.rfile.read(int(length))
            if not validate_open_code_chat(raw, model):
                self._reply(403, b'{"error":"pinned chat request denied"}')
                return
            with lock:
                if state["attempts"] >= MAX_CALLS:
                    self._reply(403, b'{"error":"inference budget exhausted"}')
                    return
                state["attempts"] += 1
            try:
                answer = request_real_text(model, digest)
            except (OSError, ValueError, RuntimeError, TypeError, http.client.HTTPException):
                self._reply(502, b'{"error":"pinned inference not completed"}')
                return
            with lock:
                state["completed"] += 1
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Content-Length", str(len(answer)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(answer)
            self.close_connection = True

    return Handler
