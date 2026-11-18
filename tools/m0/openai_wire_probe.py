#!/usr/bin/env python3
"""M0.8a: one fixed, harmless OpenAI-compatible Ollama tool-call stream.

Host-loopback preflight ONLY. Never runs model-produced tool calls or OpenCode.
Does not establish isolated transport or autonomous coding authorization.
"""
from __future__ import annotations
import argparse
import http.client
import json
import pathlib
import re
import sys
from typing import Any, BinaryIO
from common import ROOT, ollama_json, utcnow, write_json

MODEL_RE = re.compile(r"[a-zA-Z0-9_.-]+:[a-zA-Z0-9_.-]+\Z")
HOST, PORT = "127.0.0.1", 11434
PROMPT = "Call report_ready once with status READY. Do not write code."
TOOL = "report_ready"
MAX_LINE, MAX_BYTES, MAX_EVENTS = 16384, 262144, 1024


def payload(model: str) -> dict[str, Any]:
    if not MODEL_RE.fullmatch(model):
        raise ValueError("Exact local Ollama tag required")
    return {"model": model, "messages": [{"role": "user", "content": PROMPT}],
            "stream": True, "temperature": 0, "max_tokens": 256,
            "tools": [{"type": "function", "function": {
                "name": TOOL, "description": "Report a fixed value; no side effects.",
                "parameters": {"type": "object", "required": ["status"],
                               "properties": {"status": {"type": "string", "enum": ["READY"]}},
                               "additionalProperties": False}}}]}


def inspect(model: str) -> tuple[str, dict[str, bool]]:
    base = f"http://{HOST}:{PORT}"
    entries = [m for m in ollama_json(base, "/api/tags").get("models", [])
               if m.get("name") == model and m.get("digest")]
    if len(entries) != 1:
        raise ValueError("Exact local model/digest unavailable")
    info = ollama_json(base, "/api/show", {"model": model})
    catalog = ollama_json(base, "/v1/models")
    return entries[0]["digest"], {
        "exact_model": True,
        "native_tools": "tools" in info.get("capabilities", []),
        "openai_catalog": any(m.get("id") == model for m in catalog.get("data", [])),
    }


def read_sse(source: BinaryIO, model: str) -> int:
    """Fail closed unless exactly one known tool call contains the READY value."""
    events, size, done, finish, name, arguments = 0, 0, False, None, None, ""
    while events < MAX_EVENTS:
        line = source.readline(MAX_LINE + 1)
        if not line:
            break
        size += len(line)
        if len(line) > MAX_LINE or size > MAX_BYTES:
            raise ValueError("Streaming budget exceeded")
        line = line.strip()
        if not line or line.startswith(b":"):
            continue
        if not line.startswith(b"data:"):
            raise ValueError("Malformed SSE framing")
        data = line[5:].strip()
        if data == b"[DONE]":
            done = True
            break
        events += 1
        try:
            event = json.loads(data)
        except (UnicodeError, ValueError) as exc:
            raise ValueError("Malformed SSE JSON") from exc
        if not isinstance(event, dict) or event.get("model") != model:
            raise ValueError("Unexpected streamed model")
        choices = event.get("choices")
        if (not isinstance(choices, list) or len(choices) != 1
                or not isinstance(choices[0], dict) or choices[0].get("index") != 0):
            raise ValueError("Unexpected completion choice")
        choice = choices[0]
        if choice.get("finish_reason") is not None:
            finish = choice["finish_reason"]
        delta = choice.get("delta") or {}
        if not isinstance(delta, dict) or not isinstance(delta.get("tool_calls", []), list):
            raise ValueError("Malformed tool delta")
        for call in delta.get("tool_calls", []):
            if not isinstance(call, dict) or call.get("index") != 0:
                raise ValueError("Unexpected tool index")
            func = call.get("function") or {}
            if not isinstance(func, dict):
                raise ValueError("Malformed tool function")
            new_name = func.get("name")
            if new_name is not None:
                if new_name != TOOL:
                    raise ValueError("Unexpected tool name")
                name = new_name
            fragment = func.get("arguments", "")
            if not isinstance(fragment, str):
                raise ValueError("Non-text tool arguments")
            arguments += fragment
            if len(arguments) > 4096:
                raise ValueError("Tool arguments too long")
    if not done or finish != "tool_calls" or name != TOOL:
        raise ValueError("Expected tool call and complete SSE sentinel")
    try:
        args = json.loads(arguments)
    except (UnicodeError, ValueError) as exc:
        raise ValueError("Invalid tool arguments") from exc
    if args != {"status": "READY"}:
        raise ValueError("Unexpected tool arguments")
    return events


def complete(model: str) -> int:
    body = json.dumps(payload(model)).encode()
    connection = http.client.HTTPConnection(HOST, PORT, timeout=300)
    try:
        connection.request("POST", "/v1/chat/completions", body=body,
                           headers={"Content-Type": "application/json", "Connection": "close"})
        response = connection.getresponse()
        if response.status != 200:
            raise RuntimeError(f"Local inference HTTP status {response.status}")
        if "text/event-stream" not in response.getheader("Content-Type", ""):
            raise ValueError("Non-streaming Ollama response")
        return read_sse(response, model)
    finally:
        connection.close()


def probe(model: str) -> dict[str, Any]:
    report = {"schema_version": 1, "stage": "M0.8a", "recorded_at_utc": utcnow(),
              "status": "BLOCKED", "model": model,
              "purpose": "host-local OpenAI wire preflight, no agent or tool execution",
              "checks": {k: False for k in ("exact_model", "native_tools", "openai_catalog",
                                               "sse_tool_call", "digest_stable")}}
    try:
        payload(model)
        digest, checks = inspect(model)
        report["checks"].update(checks)
        report["model_digest"] = digest
        if not all(checks.values()):
            raise ValueError("Required Ollama model capabilities unavailable")
        report["event_count"] = complete(model)
        report["checks"]["sse_tool_call"] = True
        report["checks"]["digest_stable"] = inspect(model)[0] == digest
        if not all(report["checks"].values()):
            raise ValueError("Model digest changed or a required check failed")
        report["status"] = "WIRE_PASS"
    except (OSError, ValueError, RuntimeError, TypeError, KeyError) as exc:
        report["reason"] = f"{type(exc).__name__}: {str(exc)[:180]}"
    return report


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--model", default="qwen3.5:4b")
    p.add_argument("--output", type=pathlib.Path,
                   default=ROOT / "artifacts/m0/openai-wire.json")
    args = p.parse_args()
    result = probe(args.model)
    write_json(args.output, result)
    print(f'M0.8a OPENAI WIRE {result["status"]}: {args.model}')
    for key, good in result["checks"].items():
        print(f'  {key}: {"PASS" if good else "FAIL"}')
    if result.get("reason"):
        print("Reason:", result["reason"])
    print("Evidence:", args.output)
    print("No OpenCode session, generated code, or model-suggested tool was executed.")
    return 0 if result["status"] == "WIRE_PASS" else 2


if __name__ == "__main__":
    sys.exit(main())
