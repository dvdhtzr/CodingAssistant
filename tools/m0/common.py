"""Shared M0 utilities; Python 3.10+, standard library only."""
from __future__ import annotations
import datetime as dt
import hashlib
import json
import pathlib
import shutil
import subprocess
import urllib.parse
import urllib.request
from typing import Any

ROOT = pathlib.Path(__file__).resolve().parents[2]

def utcnow() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat()

def invoke(argv: list[str], *, cwd: pathlib.Path | None = None, timeout: int = 15) -> dict[str, Any]:
    try:
        out = subprocess.run(argv, cwd=cwd, text=True, encoding="utf-8", errors="replace",
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout, check=False)
        return {"exit_code": out.returncode, "stdout": out.stdout[-16000:], "stderr": out.stderr[-16000:]}
    except (OSError, subprocess.TimeoutExpired) as exc:
        return {"exit_code": None, "error": f"{type(exc).__name__}: {exc}", "stdout": "", "stderr": ""}

def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1048576), b""):
            digest.update(chunk)
    return digest.hexdigest()

def write_json(path: pathlib.Path, data: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    staging = path.with_suffix(path.suffix + ".tmp")
    staging.write_text(json.dumps(data, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    staging.replace(path)

def endpoint_url(base: str, resource: str) -> str:
    parsed = urllib.parse.urlsplit(base)
    if parsed.scheme not in ("http", "https") or not parsed.hostname:
        raise ValueError("Ollama URL requires absolute http(s)")
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise ValueError("Ollama endpoint may not contain credentials, query or fragment")
    prefix = parsed.path.rstrip("/")
    if prefix.endswith("/v1"):
        prefix = prefix[:-3]
    return urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, prefix + resource, "", ""))

def ollama_json(base: str, resource: str, payload: dict | None = None, timeout: float = 8) -> Any:
    body = json.dumps(payload).encode("utf-8") if payload is not None else None
    request = urllib.request.Request(endpoint_url(base, resource), data=body,
                                     method="POST" if payload is not None else "GET",
                                     headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=timeout) as reply:
        if reply.status != 200:
            raise RuntimeError(f"Ollama returned HTTP {reply.status}")
        return json.loads(reply.read(4000000).decode("utf-8"))

def command_version(command: str, *args: str) -> dict[str, Any]:
    executable = shutil.which(command)
    if not executable:
        return {"installed": False, "version": None}
    out = invoke([executable, *args])
    lines = (out.get("stdout") or out.get("stderr") or "").splitlines()
    return {"installed": True, "version": lines[0][:250] if lines else None,
            "exit_code": out["exit_code"]}
