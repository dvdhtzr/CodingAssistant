#!/usr/bin/env python3
"""Read-only cross-platform host and Ollama inventory."""
from __future__ import annotations
import argparse
import os
import pathlib
import platform
import shutil
import sys
from common import ROOT, command_version, invoke, ollama_json, utcnow, write_json

def collect(endpoint: str, models: list[str]) -> dict:
    disk = shutil.disk_usage(pathlib.Path.home())
    result = {
        "schema_version": 1, "recorded_at_utc": utcnow(),
        "platform": {"system": platform.system(), "release": platform.release(),
                     "machine": platform.machine(), "python": platform.python_version(),
                     "cpu_count_logical": os.cpu_count(), "disk_free_bytes": disk.free},
        "tools": {name: command_version(name, "--version")
                  for name in ("git", "dotnet", "node", "npm", "ollama", "opencode", "openclaw")},
        "ollama": {"endpoint": endpoint, "reachable": False, "models": [], "requested_models": {}}
    }
    if sys.platform.startswith("linux"):
        try:
            lines = pathlib.Path("/proc/meminfo").read_text().splitlines()
            result["platform"]["memory_total_bytes"] = int(next(
                line for line in lines if line.startswith("MemTotal:")).split()[1]) * 1024
        except (OSError, ValueError, StopIteration):
            pass
    gpu = command_version("nvidia-smi", "--version")
    result["tools"]["nvidia-smi"] = gpu
    if gpu["installed"]:
        g = invoke(["nvidia-smi", "--query-gpu=name,memory.total", "--format=csv,noheader,nounits"])
        if g["exit_code"] == 0:
            result["platform"]["gpu"] = g["stdout"].strip().splitlines()
    try:
        version = ollama_json(endpoint, "/api/version")
        inventory = ollama_json(endpoint, "/api/tags")
        result["ollama"].update({"reachable": True, "version": version.get("version"),
            "models": [{"name": x.get("name"), "digest": x.get("digest"),
                        "size_bytes": x.get("size"), "modified_at": x.get("modified_at")}
                       for x in inventory.get("models", [])]})
        for model in models:
            try:
                info = ollama_json(endpoint, "/api/show", {"model": model})
                result["ollama"]["requested_models"][model] = {
                    "available": True, "capabilities": info.get("capabilities", []),
                    "parameters": info.get("parameters", "")[:2000]}
            except Exception as exc:
                result["ollama"]["requested_models"][model] = {
                    "available": False, "error": f"{type(exc).__name__}: {exc}"}
    except Exception as exc:
        result["ollama"]["error"] = f"{type(exc).__name__}: {exc}"
    result["readiness"] = {
        "dotnet_10": (result["tools"]["dotnet"]["version"] or "").startswith("10."),
        "git_available": result["tools"]["git"]["installed"],
        "opencode_available": result["tools"]["opencode"]["installed"],
        "ollama_available": result["ollama"]["reachable"]}
    return result

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--endpoint", default="http://127.0.0.1:11434")
    ap.add_argument("--model", action="append", default=[])
    ap.add_argument("--output", type=pathlib.Path, default=ROOT / "artifacts/m0/inventory.json")
    args = ap.parse_args()
    info = collect(args.endpoint, args.model)
    write_json(args.output, info)
    print(f"Inventory written to: {args.output}")
    for key, value in info["readiness"].items():
        print(f"  {key}: {'YES' if value else 'NO'}")
    print("No software or system settings were changed.")
    return 0 if info["readiness"]["ollama_available"] else 2

if __name__ == "__main__":
    sys.exit(main())
