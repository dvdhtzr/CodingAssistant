"""M0.16 pinned real Qwen native OpenCode write broker for frozen qualification.

The model sees original task instructions and CURRENT frozen/disposable source,
never unrestricted host context. Native OpenCode "write" performs changes; the
broker only normalizes one real, strictly validated model choice at a time.
Only the exact two API/React paths (or one C# path) are writable. No shell,
Git, web, external network or generic model proxy. A fixed final reply closes
the OpenCode session; it is NOT a model-authored verification claim.
"""
from __future__ import annotations
import hashlib
import http.client
import io
import json
import pathlib
import threading
from typing import Any

from ollama_bridge import BridgeHandler
from pinned_openai import _reject_duplicates, pinned_openai_handler
from pinned_inference import verify_model_digest
from pinned_real_text import _user_text
from qualification_isolation_probe import MANIFEST

FILES = {
    "01-api-feature": ("/workspace/api/Program.cs",),
    "02-build-repair": ("/workspace/api/Program.cs",),
    "03-fullstack-feature": ("/workspace/api/Program.cs",
                             "/workspace/web/src/TaskSummary.tsx"),
}
READS = {
    "01-api-feature": ("/workspace/api/Program.cs", "/workspace/api/Api.csproj"),
    "02-build-repair": ("/workspace/api/Program.cs", "/workspace/api/Repair.csproj"),
    "03-fullstack-feature": ("/workspace/api/Program.cs", "/workspace/api/Api.csproj",
                             "/workspace/web/package.json",
                             "/workspace/web/src/App.tsx",
                             "/workspace/web/tsconfig.json"),
}
MAX_REQUEST = 1024 * 1024
MAX_SSE = 262144
MAX_ARGUMENTS = 32768
MAX_CODE = 16000
MAX_EVENTS = 2048

def task_prompt(task: str) -> str:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    matches = [x["prompt"] for x in manifest["tasks"] if x["id"] == task]
    if len(matches) != 1 or task not in FILES:
        raise ValueError("Unknown frozen task manifest")
    return matches[0]

def tool_id(stage: int) -> str:
    return f"call_m016_{stage}"

def native_write_offered(request: dict[str, Any]) -> bool:
    tools = request.get("tools")
    if type(tools) is not list:
        return False
    selected = [x.get("function") for x in tools if
                type(x) is dict and x.get("type") == "function" and
                type(x.get("function")) is dict and
                x["function"].get("name") == "write"]
    if len(selected) != 1:
        return False
    schema = selected[0].get("parameters")
    return (type(schema) is dict and type(schema.get("properties")) is dict and
            type(schema.get("required")) is list and
            all(x in schema["properties"] and x in schema["required"]
                for x in ("path", "content")))

def validate_chat(data: bytes, task: str, model: str, stage: int) -> bool:
    if not 0 < len(data) <= MAX_REQUEST:
        return False
    try:
        request = json.loads(data, object_pairs_hook=_reject_duplicates)
    except (ValueError, UnicodeError):
        return False
    if (type(request) is not dict or request.get("model") != model or
            request.get("stream") is not True or
            not native_write_offered(request)):
        return False
    msgs = request.get("messages")
    if (type(msgs) is not list or not 1 <= len(msgs) <= 64 or
            any(type(m) is not dict or m.get("role") not in (
                "system", "developer", "user", "assistant", "tool")
                for m in msgs)):
        return False
    users = [m for m in msgs if m["role"] == "user"]
    if len(users) != 1 or _user_text(users[0]).count(task_prompt(task)) != 1:
        return False
    tools = [m for m in msgs if m["role"] == "tool"]
    if len(tools) != stage:
        return False
    for i, msg in enumerate(tools):
        if (msg.get("tool_call_id") != tool_id(i) or
                "error" in _user_text(msg).lower()):
            return False
    return True

def parse_write_sse(data: bytes, model: str) -> dict[str, str]:
    if not 0 < len(data) <= MAX_SSE:
        raise ValueError("Incomplete or oversized provider response")
    fragments: list[str] = []
    name = ""
    done = False
    finish = None
    seen = False
    events = 0
    for line in io.BytesIO(data):
        if len(line) > 32768:
            raise ValueError("Provider event too long")
        line = line.strip()
        if not line or line.startswith(b":"):
            continue
        if done or not line.startswith(b"data:"):
            raise ValueError("Invalid SSE framing")
        payload = line[5:].strip()
        if payload == b"[DONE]":
            done = True
            continue
        events += 1
        if events > MAX_EVENTS:
            raise ValueError("Too many provider events")
        event = json.loads(payload, object_pairs_hook=_reject_duplicates)
        if type(event) is not dict or event.get("model") != model:
            raise ValueError("Provider model mismatch")
        choices = event.get("choices")
        if (type(choices) is not list or len(choices) != 1 or
                type(choices[0]) is not dict or choices[0].get("index") != 0):
            raise ValueError("Multiple provider choices")
        c = choices[0]
        delta = c.get("delta")
        if (type(delta) is not dict or "function_call" in delta or
                delta.get("content") not in (None, "")):
            raise ValueError("Unexpected text or function shape")
        if c.get("finish_reason") is not None:
            if finish is not None:
                raise ValueError("Repeated finish reason")
            finish = c["finish_reason"]
        calls = delta.get("tool_calls")
        if calls is None:
            continue
        if (type(calls) is not list or len(calls) != 1 or
                type(calls[0]) is not dict or calls[0].get("index") != 0 or
                calls[0].get("type") not in (None, "function")):
            raise ValueError("Unauthorized tool call")
        func = calls[0].get("function")
        if type(func) is not dict:
            raise ValueError("Invalid provider function")
        if func.get("name") is not None:
            if name:
                raise ValueError("Repeated provider function name")
            name = func["name"]
        fragment = func.get("arguments")
        if fragment is not None:
            if type(fragment) is not str:
                raise ValueError("Non-string provider arguments")
            fragments.append(fragment)
            if sum(len(x) for x in fragments) > MAX_ARGUMENTS:
                raise ValueError("Provider arguments exceed budget")
        seen = True
    if not done or not seen or finish != "tool_calls" or name != "write":
        raise ValueError("Model did not finish one native write")
    args = json.loads("".join(fragments), object_pairs_hook=_reject_duplicates)
    if (type(args) is not dict or set(args) != {"path", "content"} or
            any(type(x) is not str for x in args.values()) or
            not 0 < len(args["content"].encode("utf-8")) <= MAX_CODE or
            "\x00" in args["content"]):
        raise ValueError("Model write arguments rejected")
    return args

def authorize_write(args: object, task: str,
                    done: list[dict[str, str]]) -> bool:
    if type(args) is not dict or set(args) != {"path", "content"}:
        return False
    if type(args["path"]) is not str or type(args["content"]) is not str:
        return False
    remaining = set(FILES[task]) - {x["path"] for x in done}
    data = args["content"].encode("utf-8")
    return (args["path"] in remaining and
            0 < len(data) <= MAX_CODE and "\x00" not in args["content"])


def request_model_write(model: str, digest: str, task: str,
                        stage: int, done: list[dict[str, str]],
                        compile_feedback: tuple[str, ...] = ()) -> dict[str, str]:
    if not verify_model_digest(model, digest):
        raise ValueError("Model digest drift")
    remaining = [p for p in FILES[task] if p not in {w["path"] for w in done}]
    if len(remaining) != len(FILES[task]) - stage:
        raise ValueError("Write stage changed")
    sources = {}
    for path in READS[task]:
        value = pathlib.Path(path.replace("/workspace", "", 1))
        # Host receives a disposable workspace root explicitly via class config;
        # do not derive or traverse source paths supplied by OpenCode.
        sources[path] = (CURRENT_WORKSPACE.get() / value.relative_to("/")).read_text(
            encoding="utf-8")
    for path in FILES[task]:
        file = CURRENT_WORKSPACE.get() / path.removeprefix("/workspace/")
        if file.is_file():
            sources[path] = file.read_text(encoding="utf-8")
    if compile_feedback and (
            task not in ("01-api-feature", "02-build-repair") or
            len(compile_feedback) > 5 or any(
                type(line) is not str or len(line) > 208 or
                not __import__("re").fullmatch(r"CS[0-9]{4}: [ -~]{1,200}", line)
                for line in compile_feedback)):
        raise ValueError("Untrusted compiler diagnostics rejected")
    prompt = (
        "Implement the following ORIGINAL benchmark request without claiming tests ran. "
        "Use ONLY the native write tool (path, content) once this turn. "
        "Write a COMPLETE valid source file, no markdown, no explanation. "
        "Permitted remaining paths: " + json.dumps(remaining) + "\n"
        "Original request: " + task_prompt(task) + "\n"
        "Current disposable project files: " + json.dumps(sources) + "\n"
        "Choose ONE remaining path, fully implement its source. "
        "For full-stack implement both Program.cs and TaskSummary.tsx in separate turns.")
    if compile_feedback:
        prompt += (
            "\\nA prior independent sandboxed .NET build of THIS disposable copy "
            "failed with these compiler diagnostics: " +
            json.dumps(compile_feedback) +
            "\\nREPAIR THE SOURCE to resolve the compiler errors and satisfy "
            "the ORIGINAL task. Write the COMPLETE corrected allowed file. "
            "Do not claim that you ran any tests.")
    tool = {"type": "function", "function": {
        "name": "write", "description": "Replace one complete approved source file.",
        "parameters": {"type": "object",
                       "properties": {"path": {"type": "string"},
                                      "content": {"type": "string"}},
                       "required": ["path", "content"],
                       "additionalProperties": False}}}
    query = {"model": model, "messages": [
        {"role": "system", "content": "Produce one native write tool call only."},
        {"role": "user", "content": prompt}],
        "tools": [tool], "tool_choice": "auto", "stream": True,
        "temperature": 0, "max_tokens": 4096}
    conn = http.client.HTTPConnection("127.0.0.1", 11434, timeout=300)
    try:
        conn.request("POST", "/v1/chat/completions",
                     body=json.dumps(query, separators=(",", ":")).encode(),
                     headers={"Content-Type": "application/json",
                              "Connection": "close"})
        reply = conn.getresponse()
        if reply.status != 200 or "text/event-stream" not in reply.getheader(
                "Content-Type", ""):
            raise ValueError("Provider rejected streaming write")
        stream = reply.read(MAX_SSE + 1)
        if len(stream) > MAX_SSE:
            raise ValueError("Provider response too large")
    finally:
        conn.close()
    args = parse_write_sse(stream, model)
    if not authorize_write(args, task, done) or not verify_model_digest(model, digest):
        raise ValueError("Model chose unauthorized path or digest changed")
    return args

# A context-local, trusted path configured by the run controller; not a socket
# route. pathlib.Path is NOT imported from or passed by the LLM.
import contextvars
CURRENT_WORKSPACE: contextvars.ContextVar[pathlib.Path] = contextvars.ContextVar(
    "qualification_workspace")

def _event(model: str, delta: dict, finish: str | None = None) -> bytes:
    item = {"id": "chatcmpl-m016", "object": "chat.completion.chunk",
            "created": 1, "model": model,
            "choices": [{"index": 0, "delta": delta, "finish_reason": finish}]}
    return b"data: " + json.dumps(item, separators=(",", ":")).encode() + b"\n\n"

def write_response(model: str, stage: int, args: dict[str, str]) -> bytes:
    call = {"index": 0, "id": tool_id(stage), "type": "function",
            "function": {"name": "write",
                "arguments": json.dumps(args, separators=(",", ":"))}}
    return (_event(model, {"role": "assistant"}) +
            _event(model, {"tool_calls": [call]}) +
            _event(model, {}, "tool_calls") + b"data: [DONE]\n\n")

def final_response(model: str) -> bytes:
    return (_event(model, {"role": "assistant"}) +
            _event(model, {"content": "SOURCE WRITES COMPLETE; NOT VERIFIED"}) +
            _event(model, {}, "stop") + b"data: [DONE]\n\n")

def pinned_qualification_handler(model: str, digest: str, task: str,
                                 workspace: pathlib.Path,
                                 state: dict[str, Any],
                                 compile_feedback: tuple[str, ...] = ()) -> type[BridgeHandler]:
    if task not in FILES or workspace.is_symlink() or not workspace.is_dir():
        raise ValueError("Unapproved workspace")
    base = pinned_openai_handler(model, digest)
    lock = threading.Lock()
    class Handler(base):
        def do_POST(self) -> None:
            if self.path != "/v1/chat/completions":
                return super().do_POST()
            length = self.headers.get("Content-Length", "")
            media = self.headers.get("Content-Type", "").split(";")[0].strip().lower()
            if (not length.isascii() or not length.isdecimal() or
                    not 0 < int(length) <= MAX_REQUEST or
                    media != "application/json" or self.headers.get("Transfer-Encoding")):
                state["rejected"] += 1
                self._reply(403, b'{"error":"invalid request framing"}')
                return
            data = self.rfile.read(int(length))
            with lock:
                stage = state["requests"]
                if (stage > len(FILES[task]) or
                        not validate_chat(data, task, model, stage)):
                    state["rejected"] += 1
                    self._reply(403, b'{"error":"unexpected chat stage"}')
                    return
                # Every prior native write must have been truly performed by
                # OpenCode before we will accept its next chat request.
                for item in state["writes"]:
                    f = workspace / item["path"].removeprefix("/workspace/")
                    if (f.is_symlink() or not f.is_file() or
                            f.read_text(encoding="utf-8") != item["content"]):
                        state["rejected"] += 1
                        self._reply(403, b'{"error":"native write not observed"}')
                        return
                state["requests"] += 1
                if stage < len(FILES[task]):
                    state["attempts"] += 1
                    token = CURRENT_WORKSPACE.set(workspace)
                    try:
                        args = request_model_write(model, digest, task, stage,
                                                   state["writes"],
                                                   compile_feedback)
                        if not authorize_write(args, task, state["writes"]):
                            raise ValueError("Unauthorized model source target")
                    except (OSError, RuntimeError, ValueError, TypeError,
                            http.client.HTTPException):
                        state["model_failures"] += 1
                        self._reply(502, b'{"error":"model tool call rejected"}')
                        return
                    finally:
                        CURRENT_WORKSPACE.reset(token)
                    state["completed"] += 1
                    state["writes"].append(args)
                    response = write_response(model, stage, args)
                else:
                    if not verify_model_digest(model, digest):
                        self._reply(502, b'{"error":"model digest drift"}')
                        return
                    state["followups"] += 1
                    response = final_response(model)
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Content-Length", str(len(response)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(response)
            self.close_connection = True
    return Handler
