#!/usr/bin/env python3
"""Single Ollama tool-call probe. Does not run external tools or change files outside evidence."""
from __future__ import annotations
import argparse
import json
import pathlib
import sys
import time
from common import ROOT, ollama_json, utcnow, write_json

def correct(call: dict) -> bool:
    function = call.get("function", {})
    args = function.get("arguments", {})
    if isinstance(args, str):
        try:
            args = json.loads(args)
        except ValueError:
            return False
    return function.get("name") == "add_numbers" and isinstance(args, dict) and args.get("a") == 7 and args.get("b") == 8

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--model", required=True)
    ap.add_argument("--endpoint", default="http://127.0.0.1:11434")
    ap.add_argument("--timeout", type=int, default=180)
    ap.add_argument("--output", type=pathlib.Path,
                    default=ROOT / "artifacts/m0/inference-probe.json")
    args = ap.parse_args()
    evidence = {"schema_version": 1, "checked_at_utc": utcnow(),
                "model": args.model, "endpoint": args.endpoint, "status": "NOT_RUN"}
    try:
        details = ollama_json(args.endpoint, "/api/show", {"model": args.model})
        evidence["capabilities"] = details.get("capabilities", [])
        if "tools" not in evidence["capabilities"]:
            evidence["status"] = "NO_TOOL_CAPABILITY"
        else:
            start = time.monotonic()
            response = ollama_json(args.endpoint, "/api/chat", {
                "model": args.model, "stream": False,
                "messages": [{"role": "user", "content": "Use add_numbers to add 7 and 8. Do not answer without calling the tool."}],
                "tools": [{"type": "function", "function": {
                    "name": "add_numbers", "description": "Add two integers",
                    "parameters": {"type": "object", "properties": {
                        "a": {"type": "integer"}, "b": {"type": "integer"}},
                        "required": ["a", "b"]}}}],
                "options": {"temperature": 0}}, timeout=args.timeout)
            calls = response.get("message", {}).get("tool_calls") or []
            evidence.update({"status": "PASS" if any(correct(x) for x in calls) else "FAIL",
                "tool_call_count": len(calls), "duration_seconds": round(time.monotonic() - start, 3),
                "prompt_eval_count": response.get("prompt_eval_count"),
                "eval_count": response.get("eval_count"), "eval_duration_ns": response.get("eval_duration"),
                "model_reported": response.get("model")})
    except Exception as exc:
        evidence.update({"status": "ERROR", "error": f"{type(exc).__name__}: {exc}"})
    write_json(args.output, evidence)
    print(f'{evidence["status"]}: {args.model}; evidence: {args.output}')
    return 0 if evidence["status"] == "PASS" else 2

if __name__ == "__main__":
    sys.exit(main())
