"""M0.11: one model-authored C# repair proposal; never executes model tools.

The broker reconstructs a fixed prompt from the frozen two-file benchmark
fixture, accepts one pinned streaming text response, rejects tool calls,
and records only a strictly validated proposed Program.cs in volatile memory.
A separate no-network OS-isolated evaluator may test it later.
"""
from __future__ import annotations

import http.client
import io
import json
import re
import threading
from typing import Any

from disposable_workspace_probe import SOURCE
from ollama_bridge import BridgeHandler
from pinned_openai import _reject_duplicates, pinned_openai_handler
from pinned_inference import verify_model_digest
from pinned_real_text import _completion, _user_text

MAX_REQUEST = 1024 * 1024
MAX_STREAM = 128 * 1024
MAX_EVENTS = 1024
MAX_SOURCE = 4096


def frozen_prompt() -> str:
    source = (SOURCE / "Program.cs").read_text(encoding="utf-8")
    return (
        "Work ONLY on the following frozen C# Program.cs. Diagnose and fix its "
        "compile error with the smallest possible change while preserving "
        "existing behavior. The console program must print exactly 'Total: 15'. "
        "Return the FULL Program.cs contents, retaining all original lines, "
        "spacing, and comments except the minimum necessary code correction. "
        "No markdown, preamble, analysis, explanations, tool calls, commands, "
        "or additional files.\n\nProgram.cs:\n" + source
    )


def validate_chat_request(data: bytes, model: str, prompt: str) -> bool:
    if not 0 < len(data) <= MAX_REQUEST:
        return False
    try:
        obj = json.loads(data, object_pairs_hook=_reject_duplicates)
    except (ValueError, UnicodeError):
        return False
    if (type(obj) is not dict or obj.get("model") != model
            or obj.get("stream") is not True or
            not isinstance(obj.get("messages"), list) or
            not 1 <= len(obj["messages"]) <= 64):
        return False
    messages = obj["messages"]
    if any(type(m) is not dict or m.get("role") not in
           ("system", "developer", "user") for m in messages):
        return False
    users = [m for m in messages if m["role"] == "user"]
    return len(users) == 1 and _user_text(users[0]).count(prompt) == 1


def parse_proposal_sse(stream: bytes, model: str) -> str:
    if not 0 < len(stream) <= MAX_STREAM:
        raise ValueError("Provider streaming response over budget")
    fragments: list[str] = []
    seen_done = False
    finished = None
    n = 0
    for line in io.BytesIO(stream):
        if len(line) > 16384:
            raise ValueError("SSE line over budget")
        line = line.strip()
        if not line or line.startswith(b":"):
            continue
        if not line.startswith(b"data:"):
            raise ValueError("Unexpected SSE framing")
        raw = line[5:].strip()
        if raw == b"[DONE]":
            seen_done = True
            break
        n += 1
        if n > MAX_EVENTS:
            raise ValueError("SSE event budget exceeded")
        try:
            event = json.loads(raw)
        except (ValueError, UnicodeError) as exc:
            raise ValueError("Malformed streaming response") from exc
        if not isinstance(event, dict) or event.get("model") != model:
            raise ValueError("Unexpected provider model")
        choices = event.get("choices")
        if (not isinstance(choices, list) or len(choices) != 1 or
                type(choices[0]) is not dict or choices[0].get("index") != 0):
            raise ValueError("Unexpected provider choices")
        choice = choices[0]
        delta = choice.get("delta")
        if type(delta) is not dict or "tool_calls" in delta or "function_call" in delta:
            raise ValueError("Missing delta or model attempted a tool call")
        if choice.get("finish_reason") is not None:
            finished = choice["finish_reason"]
        part = delta.get("content")
        if part is not None:
            if type(part) is not str:
                raise ValueError("Unexpected content fragment")
            fragments.append(part)
            if sum(map(len, fragments)) > MAX_SOURCE:
                raise ValueError("Model output too large")
    if not seen_done or finished != "stop":
        raise ValueError("Provider response incomplete")
    return "".join(fragments)


def validate_candidate(raw: str, original: str) -> str:
    """Pilot-only strict diff: require the model to propose one safe correction."""
    if type(raw) is not str or not raw or len(raw.encode("utf-8")) > MAX_SOURCE:
        raise ValueError("Repair candidate absent or oversized")
    s = raw.strip()
    fence = chr(96) * 3
    if s.startswith(fence):
        expression = re.escape(fence) + r"(?:csharp|cs)?\s*\n(.*?)\n" + re.escape(fence)
        match = re.fullmatch(expression, s, re.DOTALL | re.IGNORECASE)
        if not match:
            raise ValueError("Unexpected fenced answer")
        s = match.group(1)
    if "\x00" in s:
        raise ValueError("NUL in replacement source")
    if original.count("values.Summ()") != 1:
        raise ValueError("Frozen fixture does not match the repair pilot")
    expected = original.replace("values.Summ()", "values.Sum()")
    if s.rstrip("\r\n") != expected.rstrip("\r\n"):
        raise ValueError("Model proposal changed more than one approved method")
    return expected


def request_repair_proposal(model: str, digest: str, prompt: str, original: str) -> str:
    if not verify_model_digest(model, digest):
        raise ValueError("Pinned model changed before proposal")
    fixed = {"model": model, "stream": True, "temperature": 0,
             "max_tokens": 512,
             "messages": [
                 {"role": "system", "content": "Return only corrected C# source, no tool calls."},
                 {"role": "user", "content": prompt},
             ]}
    conn = http.client.HTTPConnection("127.0.0.1", 11434, timeout=240)
    try:
        conn.request("POST", "/v1/chat/completions",
                     body=json.dumps(fixed, separators=(",", ":")).encode(),
                     headers={"Content-Type": "application/json", "Connection": "close"})
        response = conn.getresponse()
        if response.status != 200 or "text/event-stream" not in response.getheader("Content-Type", ""):
            raise ValueError("Provider did not return pinned streaming answer")
        raw = response.read(MAX_STREAM + 1)
        if len(raw) > MAX_STREAM:
            raise ValueError("Provider response over budget")
    finally:
        conn.close()
    candidate = validate_candidate(parse_proposal_sse(raw, model), original)
    if not verify_model_digest(model, digest):
        raise ValueError("Pinned model changed after proposal")
    return candidate


def pinned_repair_handler(model: str, digest: str, state: dict[str, Any]) -> type[BridgeHandler]:
    base = pinned_openai_handler(model, digest)
    lock = threading.Lock()
    prompt = frozen_prompt()
    original = (SOURCE / "Program.cs").read_text(encoding="utf-8")

    class Handler(base):
        def do_POST(self) -> None:
            if self.path != "/v1/chat/completions":
                return super().do_POST()
            length = self.headers.get("Content-Length", "")
            media = self.headers.get("Content-Type", "").split(";")[0].strip().lower()
            if (not length.isascii() or not length.isdecimal()
                    or not 0 < int(length) <= MAX_REQUEST
                    or media != "application/json" or self.headers.get("Transfer-Encoding")):
                self._reply(403, b'{"error":"invalid request"}')
                return
            body = self.rfile.read(int(length))
            if not validate_chat_request(body, model, prompt):
                self._reply(403, b'{"error":"unexpected pinned prompt"}')
                return
            with lock:
                if state["attempts"] != 0:
                    self._reply(403, b'{"error":"one-attempt budget exhausted"}')
                    return
                state["attempts"] = 1
            try:
                candidate = request_repair_proposal(model, digest, prompt, original)
            except (OSError, ValueError, RuntimeError, TypeError, http.client.HTTPException):
                self._reply(502, b'{"error":"repair proposal not accepted"}')
                return
            with lock:
                state["candidate"] = candidate
                state["completed"] = 1
            response = _completion(model, candidate)
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Content-Length", str(len(response)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(response)
            self.close_connection = True
    return Handler
