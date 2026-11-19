"""M0.13: strict one-shot real Ollama tool choice for native OpenCode edit.

The untrusted model may choose an OpenAI function call, but the broker only
forwards a completed, validated edit of the frozen C# fixture. No raw model
tools, prompt history, file paths, or additional calls are forwarded.
The second OpenCode request receives a fixed acknowledgement, not another
model inference. This is a pilot, not general-purpose agent execution.
"""
from __future__ import annotations

import http.client
import io
import json
import threading
from typing import Any

from disposable_workspace_probe import SOURCE
from ollama_bridge import BridgeHandler
from pinned_openai import _reject_duplicates, pinned_openai_handler
from pinned_inference import verify_model_digest
from pinned_real_text import _user_text

MODEL_TASK = (
    "In the disposable C# project, use only the edit tool to replace "
    "values.Summ() with values.Sum() in Program.cs. Do not use shell, write, "
    "patch, web, or other tools."
)
TOOL_ID = "call_m013_exact_edit"
EDIT_ARGS = {
    "path": "/workspace/Program.cs",
    "oldString": "values.Summ()",
    "newString": "values.Sum()",
}
MAX_REQUEST = 1024 * 1024
MAX_SSE = 128 * 1024
MAX_EVENTS = 1024
MAX_ARGUMENTS = 4096
MAX_MESSAGES = 64


def _decode(data: bytes) -> dict[str, Any]:
    if not 0 < len(data) <= MAX_REQUEST:
        raise ValueError("Chat body outside allowed size")
    request = json.loads(data, object_pairs_hook=_reject_duplicates)
    if type(request) is not dict:
        raise ValueError("Invalid chat object")
    return request


def _edit_tool_schema(tools: object) -> bool:
    if type(tools) is not list:
        return False
    matches = [tool for tool in tools if type(tool) is dict and
               tool.get("type") == "function" and
               type(tool.get("function")) is dict and
               tool["function"].get("name") == "edit"]
    if len(matches) != 1:
        return False
    params = matches[0]["function"].get("parameters")
    return (type(params) is dict and type(params.get("properties")) is dict
            and type(params.get("required")) is list
            and all(key in params["properties"] and key in params["required"]
                    for key in EDIT_ARGS))


def validate_initial_request(request: dict[str, Any], model: str) -> bool:
    if request.get("model") != model or request.get("stream") is not True:
        return False
    messages = request.get("messages")
    if (type(messages) is not list or not 1 <= len(messages) <= MAX_MESSAGES
            or any(type(m) is not dict or m.get("role") not in
                   ("system", "developer", "user") for m in messages)):
        return False
    users = [msg for msg in messages if msg["role"] == "user"]
    return (len(users) == 1 and
            _user_text(users[0]).count(MODEL_TASK) == 1 and
            _edit_tool_schema(request.get("tools")))


def validate_followup_request(request: dict[str, Any], model: str) -> bool:
    if request.get("model") != model or request.get("stream") is not True:
        return False
    messages = request.get("messages")
    if (type(messages) is not list or not 2 <= len(messages) <= MAX_MESSAGES
            or any(type(m) is not dict or m.get("role") not in
                   ("system", "developer", "user", "assistant", "tool")
                   for m in messages)):
        return False
    users = [msg for msg in messages if msg["role"] == "user"]
    tools = [msg for msg in messages if msg["role"] == "tool"]
    return (len(users) == 1 and
            _user_text(users[0]).count(MODEL_TASK) == 1 and
            len(tools) == 1 and tools[0].get("tool_call_id") == TOOL_ID and
            "error" not in _user_text(tools[0]).lower())


def parse_tool_sse(data: bytes, model: str) -> dict[str, str]:
    """Reject any extra/malformed tool, text response or incomplete stream."""
    if not 0 < len(data) <= MAX_SSE:
        raise ValueError("Provider SSE exceeds budget")
    fragments: list[str] = []
    event_count = 0
    finished = None
    done = False
    tool_seen = False
    func_name = ""
    for line in io.BytesIO(data):
        if len(line) > 16384:
            raise ValueError("Provider event too long")
        line = line.strip()
        if not line or line.startswith(b":"):
            continue
        if not line.startswith(b"data:") or done:
            raise ValueError("Malformed SSE framing")
        payload = line[5:].strip()
        if payload == b"[DONE]":
            done = True
            continue
        event_count += 1
        if event_count > MAX_EVENTS:
            raise ValueError("Provider SSE event count exceeded")
        event = json.loads(payload, object_pairs_hook=_reject_duplicates)
        if type(event) is not dict or event.get("model") != model:
            raise ValueError("Provider model mismatch")
        choices = event.get("choices")
        if (type(choices) is not list or len(choices) != 1 or
                type(choices[0]) is not dict or choices[0].get("index") != 0):
            raise ValueError("Provider returned unexpected choices")
        choice = choices[0]
        delta = choice.get("delta")
        if type(delta) is not dict or "function_call" in delta:
            raise ValueError("Provider supplied forbidden call shape")
        if delta.get("content") not in (None, ""):
            raise ValueError("Provider text mixed with tool operation")
        if choice.get("finish_reason") is not None:
            if finished is not None:
                raise ValueError("Multiple finish events")
            finished = choice["finish_reason"]
        calls = delta.get("tool_calls")
        if calls is None:
            continue
        if (type(calls) is not list or len(calls) != 1 or
                type(calls[0]) is not dict or calls[0].get("index") != 0):
            raise ValueError("Multiple or malformed model tools")
        call = calls[0]
        if call.get("type") not in (None, "function"):
            raise ValueError("Unsupported model tool")
        f = call.get("function")
        if type(f) is not dict:
            raise ValueError("Missing tool definition")
        if f.get("name") is not None:
            if func_name:
                raise ValueError("Repeated function name")
            func_name = f["name"]
        args = f.get("arguments")
        if args is not None:
            if type(args) is not str:
                raise ValueError("Invalid tool arguments fragment")
            fragments.append(args)
            if sum(len(x) for x in fragments) > MAX_ARGUMENTS:
                raise ValueError("Model arguments too large")
        tool_seen = True
    if not done or finished != "tool_calls" or not tool_seen or func_name != "edit":
        raise ValueError("Model did not complete the exact tool call")
    arguments = json.loads("".join(fragments), object_pairs_hook=_reject_duplicates)
    if (type(arguments) is not dict or arguments != EDIT_ARGS or
            any(type(v) is not str for v in arguments.values())):
        raise ValueError("Model proposed unauthorized edit")
    return arguments


def _event(model: str, delta: dict, finish: str | None = None) -> bytes:
    item = {"id": "chatcmpl-m013", "object": "chat.completion.chunk",
            "created": 1, "model": model,
            "choices": [{"index": 0, "delta": delta, "finish_reason": finish}]}
    return b"data: " + json.dumps(item, separators=(",", ":")).encode() + b"\n\n"


def edit_completion(model: str) -> bytes:
    call = {"index": 0, "id": TOOL_ID, "type": "function",
            "function": {"name": "edit",
                         "arguments": json.dumps(EDIT_ARGS, separators=(",", ":"))}}
    return (_event(model, {"role": "assistant"}) +
            _event(model, {"tool_calls": [call]}) +
            _event(model, {}, "tool_calls") + b"data: [DONE]\n\n")


def finish_completion(model: str) -> bytes:
    # The final acknowledgement is deterministic: only the edit decision came
    # from the model. The caller independently checks bytes and build behavior.
    return (_event(model, {"role": "assistant"}) +
            _event(model, {"content": "EDIT COMPLETE"}) +
            _event(model, {}, "stop") + b"data: [DONE]\n\n")


def request_real_edit(model: str, digest: str) -> None:
    if not verify_model_digest(model, digest):
        raise ValueError("Model digest drift before tool decision")
    original = (SOURCE / "Program.cs").read_text(encoding="utf-8")
    if original.count("values.Summ()") != 1:
        raise ValueError("Frozen fixture does not match expected compiler defect")
    # Rebuild the only upstream request; discard OpenCode history and all of its
    # advertised tools. The model sees only the fixed source and ONE edit tool.
    prompt = ("Fix the incorrect C# method in the following frozen file by "
              "calling the provided edit tool exactly once. Use path "
              "/workspace/Program.cs, preserve all other source bytes. "
              "Return a tool call only, no explanation.\n\n" + original)
    tool = {"type": "function", "function": {
        "name": "edit", "description": "Replace one exact substring in Program.cs.",
        "parameters": {"type": "object", "properties": {
            "path": {"type": "string"},
            "oldString": {"type": "string"},
            "newString": {"type": "string"},
        }, "required": list(EDIT_ARGS), "additionalProperties": False},
    }}
    data = {"model": model, "stream": True, "temperature": 0,
            "max_tokens": 512, "tools": [tool], "tool_choice": "auto",
            "messages": [
                {"role": "system", "content":
                    "Choose the only allowed edit tool; do not output text or other tools."},
                {"role": "user", "content": prompt},
            ]}
    conn = http.client.HTTPConnection("127.0.0.1", 11434, timeout=240)
    try:
        conn.request("POST", "/v1/chat/completions",
                     body=json.dumps(data, separators=(",", ":")).encode(),
                     headers={"Content-Type": "application/json", "Connection": "close"})
        response = conn.getresponse()
        if (response.status != 200 or
                "text/event-stream" not in response.getheader("Content-Type", "")):
            raise ValueError("Provider did not stream real model tool call")
        output = response.read(MAX_SSE + 1)
        if len(output) > MAX_SSE:
            raise ValueError("Model response exceeded budget")
    finally:
        conn.close()
    parse_tool_sse(output, model)
    if not verify_model_digest(model, digest):
        raise ValueError("Model digest drift after tool decision")


def pinned_real_edit_handler(model: str, digest: str,
                             state: dict[str, int]) -> type[BridgeHandler]:
    base = pinned_openai_handler(model, digest)
    lock = threading.Lock()

    class Handler(base):
        def do_POST(self) -> None:
            if self.path != "/v1/chat/completions":
                return super().do_POST()
            length = self.headers.get("Content-Length", "")
            media = self.headers.get("Content-Type", "").split(";")[0].strip().lower()
            if (not length.isascii() or not length.isdecimal()
                    or not 0 < int(length) <= MAX_REQUEST or
                    media != "application/json" or self.headers.get("Transfer-Encoding")):
                self._reply(403, b'{"error":"invalid request framing"}')
                return
            try:
                request = _decode(self.rfile.read(int(length)))
            except (ValueError, TypeError, UnicodeError):
                self._reply(403, b'{"error":"invalid request"}')
                return
            with lock:
                stage = state["requests"]
                if stage >= 2:
                    self._reply(403, b'{"error":"chat budget exhausted"}')
                    return
                valid = (validate_initial_request(request, model) if stage == 0
                         else validate_followup_request(request, model))
                if stage == 1 and state["model_completed"] != 1:
                    valid = False
                if not valid:
                    self._reply(403, b'{"error":"unexpected chat stage"}')
                    return
                # Reserve the request budget before slow inference. No parallel
                # or repeated model calls can race the first operation.
                state["requests"] += 1
            if stage == 0:
                state["model_attempts"] += 1
                try:
                    request_real_edit(model, digest)
                except (OSError, RuntimeError, ValueError, TypeError,
                        http.client.HTTPException):
                    self._reply(502, b'{"error":"real model tool decision rejected"}')
                    return
                state["model_completed"] += 1
                answer = edit_completion(model)
            else:
                if not verify_model_digest(model, digest):
                    self._reply(502, b'{"error":"pinned model changed"}')
                    return
                state["followups"] += 1
                answer = finish_completion(model)
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Content-Length", str(len(answer)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(answer)
            self.close_connection = True

    return Handler
