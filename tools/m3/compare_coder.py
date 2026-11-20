#!/usr/bin/env python3
"""Bounded, local-only Qwen Coder vs current Qwen performance probe.

Does NOT run OpenCode, write source, execute generated code, change the
security-pinned M1 model, or claim agent qualification. Invoke only when no
Mission Control coding run is active:

    python3 tools/m3/compare_coder.py --pull-coder

--pull-coder explicitly opts into a one-time Ollama model download. Every
inference request itself goes ONLY to http://127.0.0.1:11434.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[2]
OLLAMA = "http://127.0.0.1:11434"
BASELINE = "qwen3.5:4b"
CODER = "qwen2.5-coder:3b"
ALLOWED = (BASELINE, CODER)
MAX_RESPONSE = 262144
PROMPT = """Repair this C# method. It should add, not subtract, two integers.
Call the submit_repair tool once with the full corrected C# source.
public static int Add(int left, int right) { return left - right; }"""
TOOLS = [{
    "type": "function",
    "function": {
        "name": "submit_repair",
        "description": "Submit the fixed C# method; no other tool use.",
        "parameters": {
            "type": "object",
            "properties": {"source": {"type": "string"}},
            "required": ["source"]
        }
    }
}]


class ProbeBlocked(RuntimeError):
    pass


def local_request(path: str, document: dict | None = None, *, timeout: int = 15) -> dict:
    if path not in ("/api/tags", "/api/chat"):
        raise ProbeBlocked("Refusing non-allowlisted Ollama route")
    request = urllib.request.Request(
        OLLAMA + path,
        data=(json.dumps(document, separators=(",", ":")).encode("utf-8")
              if document is not None else None),
        headers={"Content-Type": "application/json"},
        method="POST" if document is not None else "GET",
    )
    # Do not inherit HTTP_PROXY for loopback model requests.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(request, timeout=timeout) as response:
        if response.status != 200:
            raise ProbeBlocked("Local Ollama returned non-success status")
        content = response.read(MAX_RESPONSE + 1)
        if len(content) > MAX_RESPONSE:
            raise ProbeBlocked("Oversized local Ollama result")
    value = json.loads(content)
    if not isinstance(value, dict):
        raise ProbeBlocked("Invalid local Ollama result")
    return value


def local_models() -> set[str]:
    data = local_request("/api/tags")
    return {entry["name"] for entry in data.get("models", [])
            if isinstance(entry, dict) and type(entry.get("name")) is str}


def pull_coder() -> None:
    print(f"Downloading {CODER} from Ollama's model library (explicit opt-in).",
          flush=True)
    result = subprocess.run(["ollama", "pull", CODER], stdin=subprocess.DEVNULL,
                            timeout=1800, check=False)
    if result.returncode != 0:
        raise ProbeBlocked("Coder model download failed; no coding run started")


def extract_patch(response: dict) -> tuple[bool, bool]:
    message = response.get("message")
    if not isinstance(message, dict):
        return False, False
    calls = message.get("tool_calls")
    if not isinstance(calls, list) or len(calls) != 1:
        return False, False
    fn = calls[0].get("function", {}) if isinstance(calls[0], dict) else {}
    if not isinstance(fn, dict) or fn.get("name") != "submit_repair":
        return False, False
    args = fn.get("arguments")
    if isinstance(args, str):
        try:
            args = json.loads(args)
        except json.JSONDecodeError:
            return True, False
    if not isinstance(args, dict) or type(args.get("source")) is not str:
        return True, False
    source = args["source"]
    # Deterministic tiny check; not a compilation or independent acceptance.
    correct = bool(re.search(
        r"public\s+static\s+int\s+Add\s*\(\s*int\s+left\s*,\s*int\s+right\s*\)"
        r"\s*\{\s*return\s+left\s*\+\s*right\s*;\s*\}", source))
    return True, correct


def run_once(model: str, timeout: int) -> dict:
    if model not in ALLOWED:
        raise ProbeBlocked("Model is not on the bounded comparison shortlist")
    request = {
        "model": model,
        "stream": False,
        "think": False,
        "messages": [
            {"role": "system", "content": "You are a C# compiler repair assistant. "
             "Use only the submit_repair function. Do not run any code."},
            {"role": "user", "content": PROMPT},
        ],
        "tools": TOOLS,
        "options": {"temperature": 0, "num_predict": 160, "num_ctx": 4096},
    }
    started = time.monotonic()
    response = local_request("/api/chat", request, timeout=timeout)
    seconds = round(time.monotonic() - started, 2)
    called, repaired = extract_patch(response)
    tokens = response.get("eval_count")
    eval_ns = response.get("eval_duration")
    rate = (round(tokens / (eval_ns / 1e9), 2)
            if type(tokens) is int and type(eval_ns) is int and eval_ns > 0
            else None)
    return {
        "model": model,
        "elapsed_seconds": seconds,
        "generated_tokens": tokens if type(tokens) is int else None,
        "tokens_per_second": rate,
        "tool_call": called,
        "simple_repair": repaired,
        "completed": bool(response.get("done")),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pull-coder", action="store_true",
                        help="Explicitly download qwen2.5-coder:3b if missing")
    parser.add_argument("--timeout", type=int, default=180,
                        help="Maximum per-model inference wait in seconds (30..600)")
    args = parser.parse_args()
    if not 30 <= args.timeout <= 600:
        parser.error("Timeout must be between 30 and 600 seconds")
    try:
        models = local_models()
        if CODER not in models and args.pull_coder:
            pull_coder()
            models = local_models()
        missing = [name for name in ALLOWED if name not in models]
        if missing:
            raise ProbeBlocked("Models missing from local Ollama: " + ", ".join(missing)
                               + ". Add --pull-coder for the coder model.")
        print("Single short tool-call repair probe per model — no agent and no code execution.")
        results = []
        for model in ALLOWED:
            print("Testing", model, "(bounded local inference)...", flush=True)
            try:
                result = run_once(model, args.timeout)
            except (urllib.error.URLError, TimeoutError, ValueError) as exc:
                result = {"model": model, "error": type(exc).__name__}
            results.append(result)
            print("  seconds:", result.get("elapsed_seconds", "unavailable"),
                  "tokens/s:", result.get("tokens_per_second", "unavailable"),
                  "tool:", result.get("tool_call", False),
                  "simple fix:", result.get("simple_repair", False), flush=True)
        output = ROOT / "artifacts/m3"
        output.mkdir(parents=True, exist_ok=True)
        target = output / ("coder-comparison-" +
                          dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ") +
                          ".json")
        # Only bounded numeric/boolean metrics, no raw prompts, source or model replies.
        target.write_text(json.dumps(
            {"schema": "coder-local-probe-v1", "not_agent_qualification": True,
             "results": results}, indent=2) + "\n", encoding="utf-8")
        print("Local comparison metrics:", target)
        return 0 if all(r.get("completed") for r in results) else 2
    except (ProbeBlocked, OSError, ValueError, TimeoutError,
            urllib.error.URLError, json.JSONDecodeError,
            subprocess.TimeoutExpired) as exc:
        print("CODER COMPARISON BLOCKED:", type(exc).__name__, str(exc),
              file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
