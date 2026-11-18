#!/usr/bin/env python3
"""M0.3: verify OpenCode discovers the selected local Ollama model.

Does not invoke an AI agent, execute generated code, or modify project files.
OpenCode itself may refresh its local discovery cache.
"""
from __future__ import annotations

import argparse
import json
import pathlib
import re
import shutil
import subprocess
import sys
from typing import Any

from common import ROOT, command_version, ollama_json, utcnow, write_json

ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")


def model_ids(output: str) -> set[str]:
    """Collect exact provider/model identifiers from OpenCode's line output."""
    candidates = set()
    for raw in output.splitlines():
        line = ANSI.sub("", raw).strip()
        if re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.:/+-]+", line):
            candidates.add(line)
    return candidates


def analyze(model: str, tags: dict, details: dict, cli_output: str, exit_code: int) -> dict[str, Any]:
    tag = next((entry for entry in tags.get("models", [])
                if model in (entry.get("name"), entry.get("model"))), None)
    advertised = details.get("capabilities") or []
    target = f"ollama/{model}"
    discovered = model_ids(cli_output)
    checks = {
        "exact_model_downloaded": bool(tag and tag.get("digest")),
        "ollama_completion_supported": "completion" in advertised,
        "ollama_tool_calls_supported": "tools" in advertised,
        "opencode_models_command_succeeded": exit_code == 0,
        "exact_model_discovered": target in discovered,
    }
    return {"status": "PASS" if all(checks.values()) else "BLOCKED",
            "checks": checks, "model": model, "model_id": target,
            "model_digest": (tag or {}).get("digest"),
            "capabilities": advertised,
            "ollama_model_count": len(tags.get("models", [])),
            "opencode_discovered_ollama_count": sum(x.startswith("ollama/") for x in discovered)}


def probe(model: str, endpoint: str, timeout: int) -> dict[str, Any]:
    result: dict[str, Any] = {"schema_version": 1, "observed_at_utc": utcnow(),
                               "status": "BLOCKED", "model": model, "ollama_endpoint": endpoint,
                               "operation": "model_discovery_only"}
    cli = command_version("opencode", "--version")
    result["opencode_version"] = cli["version"]
    if not cli["installed"] or cli["exit_code"] != 0:
        result["reason"] = "OpenCode CLI missing or unable to report its version"
        return result
    try:
        tags = ollama_json(endpoint, "/api/tags")
        details = ollama_json(endpoint, "/api/show", {"model": model})
        version = ollama_json(endpoint, "/api/version")
        result["ollama_version"] = version.get("version")
    except Exception as exc:
        result["reason"] = f"Ollama inspection failed: {type(exc).__name__}: {exc}"
        return result

    # Inline config pins discovery to one Ollama endpoint. Providers other than Ollama
    # are denied for this invocation. This is not an OS-level security boundary.
    from common import endpoint_url
    base = endpoint_url(endpoint, "/v1")
    config = {
        "$schema": "https://opencode.ai/config.json",
        "providers": {"ollama": {"settings": {"baseURL": base}}},
        "policies": [
            {"action": "provider.use", "resource": "*", "effect": "deny"},
            {"action": "provider.use", "resource": "ollama", "effect": "allow"}
        ],
    }
    import os
    env = os.environ.copy()
    env["OPENCODE_CONFIG_CONTENT"] = json.dumps(config)
    executable = shutil.which("opencode")
    assert executable is not None
    try:
        completed = subprocess.run([executable, "models"], cwd=ROOT,
                                    env=env, check=False, capture_output=True,
                                    text=True, encoding="utf-8", errors="replace",
                                    timeout=timeout)
    except (OSError, subprocess.TimeoutExpired) as exc:
        result["reason"] = f"OpenCode discovery command failed: {type(exc).__name__}: {exc}"
        return result
    result.update(analyze(model, tags, details, completed.stdout, completed.returncode))
    result["opencode_version"] = cli["version"]
    result["ollama_version"] = version.get("version")
    if result["status"] != "PASS":
        result["reason"] = "One or more model discovery checks failed; see checks and run opencode models locally"
        # Never store the complete OpenCode catalog or local provider credentials.
        result["stderr_tail"] = completed.stderr[-700:]
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", required=True, help="Exact Ollama tag, e.g. qwen3.5:4b")
    parser.add_argument("--endpoint", default="http://127.0.0.1:11434")
    parser.add_argument("--timeout", type=int, default=90)
    parser.add_argument("--output", type=pathlib.Path,
                        default=ROOT / "artifacts/m0/opencode-discovery.json")
    args = parser.parse_args()
    if args.model.startswith("ollama/") or "/" in args.model or not args.model.strip():
        parser.error("Use only the Ollama tag, for example qwen3.5:4b")
    if not 10 <= args.timeout <= 600:
        parser.error("--timeout must be 10–600 seconds")
    report = probe(args.model, args.endpoint, args.timeout)
    write_json(args.output, report)
    print(f'M0.3 DISCOVERY {report["status"]}: {args.model}')
    for label, passed in report.get("checks", {}).items():
        print(f'  {label}: {"PASS" if passed else "FAIL"}')
    if report.get("reason"):
        print("Reason:", report["reason"])
    print("Evidence:", args.output)
    print("This check did NOT run an autonomous coding agent.")
    return 0 if report["status"] == "PASS" else 2


if __name__ == "__main__":
    sys.exit(main())
