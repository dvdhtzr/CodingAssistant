#!/usr/bin/env python3
"""CodingAssistant MVP: local model -> review diff -> approved disposable edit.

This is an interactive SINGLE-FILE assistant, not an autonomous coding agent.
It never writes to the original project or executes model-provided commands.
Python 3.10+ standard library; Ollama on 127.0.0.1.
"""
from __future__ import annotations

import argparse
import datetime as dt
import difflib
import hashlib
import json
import os
from pathlib import Path, PurePath
import re
import shutil
import subprocess
import sys
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts" / "mvp"
OLLAMA = "http://127.0.0.1:11434"
MODEL_RE = re.compile(r"[A-Za-z0-9_.-]+:[A-Za-z0-9_.-]+\Z")
SOURCE_EXT = {".cs", ".py", ".ts", ".tsx", ".js", ".jsx", ".go", ".rs"}
COPY_EXT = SOURCE_EXT | {".csproj", ".sln", ".slnx", ".css", ".html"}
COPY_NAMES = {"package.json", "tsconfig.json", "vite.config.ts"}
SKIP_DIRS = {".git", ".github", ".idea", ".vs", ".venv", "venv",
             "node_modules", "bin", "obj", "dist", "build", "artifacts",
             "__pycache__", ".next", ".nuget"}
MAX_FILE = 24_000
MAX_PROJECT = 4_000_000
MAX_FILES = 250
MAX_RESPONSE = 120_000
MAX_DIFF_LINES = 300
MAX_AUTO_RETRIES = 2
COMPILER_ERROR_RE = re.compile(r"\berror\s+CS\d+\b", re.IGNORECASE)
RETRY_ID_RE = re.compile(r"\d{8}T\d{6}Z-[0-9a-f]{8}\Z")


def json_http(path: str, post: dict | None = None, timeout: int = 180) -> dict:
    data = None if post is None else json.dumps(post).encode("utf-8")
    req = urllib.request.Request(
        OLLAMA + path, data=data,
        headers={"Content-Type": "application/json"},
        method="GET" if data is None else "POST",
    )
    with urllib.request.urlopen(req, timeout=timeout) as response:
        raw = response.read(MAX_RESPONSE + 1)
    if len(raw) > MAX_RESPONSE:
        raise ValueError("Local model response exceeded size limit")
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise ValueError("Unexpected Ollama response")
    return value


def select_source(project: Path, relative: str) -> tuple[Path, Path]:
    if project.is_symlink() or not project.is_dir():
        raise ValueError("Project must be an ordinary local directory")
    rel = PurePath(relative)
    if rel.is_absolute() or any(part in ("..", ".", "") for part in rel.parts):
        raise ValueError("Choose a file relative to the project without '..'")
    if rel.suffix.lower() not in SOURCE_EXT:
        raise ValueError("MVP accepts one source file (.cs, .py, .ts, .tsx, .js, .jsx, .go, .rs)")
    target = project.joinpath(*rel.parts)
    cur = project
    for part in rel.parts:
        cur = cur / part
        if cur.is_symlink():
            raise ValueError("Symlinks are not accepted")
    if not target.is_file() or not target.resolve().is_relative_to(project.resolve()):
        raise ValueError("Source file missing or outside project")
    if not 0 < target.stat().st_size <= MAX_FILE:
        raise ValueError("Source file is empty or too large for MVP")
    return rel, target


def stage_project(project: Path, destination: Path) -> None:
    """Copy only ordinary source/project files, never symlinks, secrets, or build output."""
    count = 0
    total = 0
    for folder, dirs, files in os.walk(project, topdown=True, followlinks=False):
        base = Path(folder)
        dirs[:] = [name for name in dirs
                   if name not in SKIP_DIRS and not name.startswith(".")
                   and not (base / name).is_symlink()]
        for name in files:
            source = base / name
            if (source.is_symlink() or not source.is_file()
                    or source.stat().st_nlink != 1
                    or (source.suffix.lower() not in COPY_EXT
                        and name not in COPY_NAMES)):
                continue
            size = source.stat().st_size
            if size > MAX_FILE:
                continue
            count += 1
            total += size
            if count > MAX_FILES or total > MAX_PROJECT:
                raise ValueError("Project exceeds MVP staging limits")
            destination_file = destination / source.relative_to(project)
            destination_file.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, destination_file)
    if count == 0:
        raise ValueError("No source files could be staged")


def normalize_proposal(text: str) -> str:
    result = text.strip()
    marker = chr(96) * 3
    if result.startswith(marker):
        lines = result.splitlines()
        if len(lines) < 3 or not lines[-1].strip() == marker:
            raise ValueError("Incomplete Markdown code fence")
        if not re.fullmatch(r" *" + re.escape(marker) + r"(?:[A-Za-z0-9#+._-]*) *",
                            lines[0]):
            raise ValueError("Unexpected model formatting")
        result = "\n".join(lines[1:-1])
    if not result or "\x00" in result or len(result.encode("utf-8")) > MAX_RESPONSE:
        raise ValueError("Model did not return a valid source file")
    return result.rstrip("\r\n") + "\n"


def propose(model: str, filename: str, source: str, task: str, *,
            expected_digest: str | None = None,
            metadata: dict | None = None) -> str:
    catalog = json_http("/api/tags")
    matches = [m for m in catalog.get("models", [])
               if isinstance(m, dict) and m.get("name") == model and m.get("digest")]
    if len(matches) != 1:
        raise ValueError(f"Exact local model {model!r} is not installed")
    digest = matches[0]["digest"]
    if expected_digest is not None and digest != expected_digest:
        raise ValueError("Local model digest changed between correction attempts")
    prompt = (f"Programming task: {task}\n"
              f"File: {filename}\n"
              "Return the COMPLETE replacement file, not a patch. "
              "Do not include explanation, Markdown, other files, or commands. "
              "Preserve everything unrelated to the task.\n"
              "--- ORIGINAL FILE ---\n" + source + "\n--- END FILE ---")
    request = {"model": model, "stream": False,
               "temperature": 0, "max_tokens": 8192,
               "messages": [
                   {"role": "system", "content":
                    "You are a careful source-code editor. Respond with only the full replacement file."},
                   {"role": "user", "content": prompt},
               ]}
    answer = json_http("/v1/chat/completions", request, timeout=300)
    if answer.get("model") != model:
        raise ValueError("Ollama returned a different model; refusing proposal")
    choices = answer.get("choices", [])
    if not isinstance(choices, list) or len(choices) != 1:
        raise ValueError("Expected one model response")
    message = choices[0].get("message", {})
    if (not isinstance(message, dict) or message.get("tool_calls")
            or choices[0].get("finish_reason") not in ("stop", None)):
        raise ValueError("Model returned incomplete output or attempted a tool call")
    content = message.get("content")
    if not isinstance(content, str):
        raise ValueError("Model returned no source text")
    after = json_http("/api/tags")
    if not any(m.get("name") == model and m.get("digest") == digest
               for m in after.get("models", []) if isinstance(m, dict)):
        raise ValueError("Local model digest changed during proposal")
    if metadata is not None:
        metadata["digest"] = digest
    return normalize_proposal(content)


def diff_lines(before: str, after: str, name: str) -> list[str]:
    return list(difflib.unified_diff(
        before.splitlines(keepends=True), after.splitlines(keepends=True),
        fromfile="original/" + name, tofile="copy/" + name,
    ))


def _container_command(workspace: Path, image_id: str, project_file: str,
                       operation: str, *, uid: int = 1000, gid: int = 1000) -> list[str]:
    if operation not in ("restore", "build", "run"):
        raise ValueError("Unsupported fixed build step")
    if not re.fullmatch(r"(?:sha256:)?[a-fA-F0-9]{64}", image_id):
        raise ValueError("Container image must be referenced by immutable ID")
    common = [
        "podman", "run", "--rm", "--pull=never", "--network=none",
        "--read-only", "--cap-drop=all", "--security-opt=no-new-privileges",
        "--pids-limit=64", "--memory=2g", "--userns=keep-id",
        "--user", f"{uid}:{gid}",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup",
        "--volume", f"{workspace.resolve()}:/workspace:rw",
        "--env", "HOME=/tmp", "--env", "DOTNET_CLI_HOME=/tmp",
        "--env", "NUGET_PACKAGES=/tmp/.nuget/packages",
        "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
        "--workdir=/workspace", "--entrypoint=dotnet", image_id,
    ]
    if operation == "restore":
        return common + ["restore", project_file, "--source",
                         "/workspace/.mvp-empty-feed", "-p:NuGetAudit=false",
                         "--disable-parallel", "-v:q"]
    if operation == "build":
        return common + ["build", project_file, "--no-restore",
                         "-c", "Release", "-p:UseSharedCompilation=false", "-v:q"]
    return common + ["run", "--project", project_file,
                     "--no-build", "-c", "Release"]


def isolated_build(workspace: Path, demo: bool, *, feedback: list[str] | None = None) -> bool:
    """Fixed dotnet commands only, in a rootless network-denied Podman container."""
    if sys.platform != "linux" or not hasattr(os, "getuid") or shutil.which("podman") is None:
        print("Isolated build is currently Linux/Podman only; no host build was run.")
        return False
    if os.getuid() == 0:
        print("Isolated build refuses root execution; use normal rootless Podman.")
        return False
    projects = list(workspace.glob("*.csproj"))
    if len(projects) != 1:
        print("Build skipped: select a directory containing exactly one top-level .csproj.")
        return False
    image = subprocess.run(
        ["podman", "image", "inspect", "localhost/codingassistant-m0-runtime:2.3.0",
         "--format", "{{.Id}}"],
        capture_output=True, text=True, timeout=15, check=False,
    )
    image_id = image.stdout.strip()
    if image.returncode != 0 or not re.fullmatch(r"(?:sha256:)?[a-fA-F0-9]{64}", image_id):
        print("Build skipped: verified local M0.7 runtime image not found.")
        return False
    (workspace / ".mvp-empty-feed").mkdir(exist_ok=True)
    for operation in (["restore", "build", "run"] if demo else ["restore", "build"]):
        result = subprocess.run(
            _container_command(workspace, image_id, projects[0].name, operation,
                               uid=os.getuid(), gid=os.getgid()),
            stdin=subprocess.DEVNULL, capture_output=True, text=True,
            timeout=120, check=False,
        )
        summary = (result.stdout + "\n" + result.stderr).strip().splitlines()
        if feedback is not None and operation == "build" and result.returncode != 0:
            # A failed restore, missing image or a failing program is not a
            # C# compiler diagnostic and must never trigger model inference.
            feedback.extend(line[:300] for line in summary[-48:]
                            if COMPILER_ERROR_RE.search(line))
        print(f"\nIsolated dotnet {operation}: {'PASS' if result.returncode == 0 else 'FAIL'}")
        for line in summary[-12:]:
            print("  " + line[:200])
        if result.returncode != 0:
            return False
    return True


def repair_task(compiler_errors: list[str], objective: str) -> str:
    """Give the model only actual bounded C# errors and the original objective."""
    return (
        "Fix these C# compiler errors in the previously approved file. "
        "Preserve the intended behavior and original programming objective. "
        "All using directives must precede top-level statements; "
        "avoid unnecessary imports when implicit usings are enabled. "
        "Return only the COMPLETE corrected source.\n"
        "Original objective: " + objective[:1200] + "\n"
        "Compiler output:\n" + "\n".join(compiler_errors)[-2500:]
    )


def save_result(run_dir: Path, record: dict) -> None:
    (run_dir / "result.json").write_text(
        json.dumps(record, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )


def run(args: argparse.Namespace) -> int:
    if not MODEL_RE.fullmatch(args.model):
        raise ValueError("Specify an exact local Ollama model tag, e.g. qwen3.5:4b")

    demo_project = ROOT / "benchmarks/fixtures/02-build-repair/api"
    retry = getattr(args, "retry", None)
    pinned_digest: str | None = None
    parent_run_id: str | None = retry
    if retry:
        if not RETRY_ID_RE.fullmatch(retry):
            raise ValueError("Retry must name one exact previously approved MVP run ID")
        run_directory = ARTIFACTS / retry
        if run_directory.is_symlink() or not run_directory.is_dir():
            raise ValueError("Approved disposable run directory was not found")
        previous_result = run_directory / "result.json"
        if previous_result.is_symlink():
            raise ValueError("Recorded result must not be a symlink")
        if previous_result.is_file():
            previous = json.loads(previous_result.read_text(encoding="utf-8"))
            if previous.get("model") != args.model:
                raise ValueError(
                    "Retry model differs from recorded model; use the original --model"
                )
            pinned_digest = previous.get("model_digest")
        project = run_directory / "project"
        if project.is_symlink() or not project.is_dir():
            raise ValueError("Approved disposable project must be an ordinary directory")
        relative = args.file or "Program.cs"
        print(f"Rechecking existing disposable copy: {project}")
        compiler_errors: list[str] = []
        if isolated_build(project, False, feedback=compiler_errors):
            print("Existing disposable copy already builds. No retry needed.")
            if getattr(args, "run", False):
                return 0 if isolated_build(project, True) else 2
            return 0
        if not compiler_errors:
            raise ValueError("No compiler diagnostics available; check rootless Podman before retrying")
        objective = args.task or "Preserve the intended C# repair and behavior."
        task = repair_task(compiler_errors, objective)
    else:
        project = demo_project if args.demo else Path(args.project).expanduser().resolve()
        relative = "Program.cs" if args.demo else args.file
        objective = (
            "Fix the C# compile error. Preserve output Total: 15. "
            "Do not add unnecessary using directives; this project enables implicit usings."
        ) if args.demo else args.task
        task = objective

    if not isinstance(objective, str) or not 0 < len(objective) <= 4000:
        raise ValueError("Supply a task description within 4000 characters")

    verify = bool(args.build or retry or getattr(args, "run", False))
    seen_failures: set[tuple[str, ...]] = set()

    # The initial proposal is not an automatic retry. Up to two *additional*
    # corrections may follow real C# compiler failures, always with approval.
    for correction in range(MAX_AUTO_RETRIES + 1):
        rel, selected = select_source(project, relative)
        original = selected.read_text(encoding="utf-8")
        print(f"Local model: {args.model}\nFile: {rel}\nTask: {task}")
        print("Requesting a replacement from local Ollama (no coding tools)...")
        model_info: dict = {}
        proposed = propose(
            args.model, str(rel), original, task,
            expected_digest=pinned_digest, metadata=model_info,
        )
        if model_info.get("digest"):
            pinned_digest = model_info["digest"]
        changes = diff_lines(original, proposed, str(rel))
        if not changes:
            print("\nNo file changes proposed. Stopping.")
            return 1
        if len(changes) > MAX_DIFF_LINES:
            print(f"\nDiff has {len(changes)} lines; exceeds review limit. Nothing applied.")
            return 2
        print("\n" + "".join(changes))
        decision = input("\nApply this diff to a DISPOSABLE COPY only? Type APPLY: ").strip()
        if decision != "APPLY":
            print("Rejected. Original project and earlier approved copies unchanged.")
            return 1

        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        identifier = (
            dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
            + "-" + uuid.uuid4().hex[:8]
        )
        workspace = ARTIFACTS / identifier / "project"
        workspace.mkdir(parents=True, exist_ok=False)
        stage_project(project, workspace)
        output = workspace.joinpath(*rel.parts)
        if not output.is_file():
            raise ValueError("Selected file was not staged")
        if output.read_text(encoding="utf-8") != original:
            raise ValueError("Input changed during staging; refusing edit")
        output.write_text(proposed, encoding="utf-8")
        (workspace.parent / "change.patch").write_text(
            "".join(changes), encoding="utf-8"
        )
        record = {
            "run_id": identifier,
            "parent_run_id": parent_run_id,
            "correction_attempt": correction,
            "model": args.model,
            "model_digest": pinned_digest,
            "file": str(rel),
            "before_sha256": hashlib.sha256(original.encode("utf-8")).hexdigest(),
            "after_sha256": hashlib.sha256(proposed.encode("utf-8")).hexdigest(),
            "status": "approved",
            "compiler_diagnostics": [],
        }
        save_result(workspace.parent, record)
        print(f"\nApplied to COPY: {output}")
        print(f"Saved patch:     {workspace.parent / 'change.patch'}")
        print(f"Original left unchanged: {selected}")

        if not verify or output.suffix.lower() != ".cs":
            return 0

        print("\nRunning fixed .NET checks inside rootless, no-network Podman.")
        compiler_errors = []
        passed = isolated_build(
            workspace, args.demo or getattr(args, "run", False),
            feedback=compiler_errors,
        )
        record["status"] = "build_passed" if passed else "build_failed"
        record["compiler_diagnostics"] = compiler_errors
        save_result(workspace.parent, record)
        if passed:
            print("Verified .NET checks passed.")
            return 0
        if not compiler_errors:
            print("Build or runtime verification failed without C# compiler diagnostics; "
                  "no automatic retry.")
            return 2
        signature = tuple(compiler_errors)
        if signature in seen_failures:
            print("Compiler diagnostics repeated without progress; stopping.")
            return 2
        seen_failures.add(signature)
        if correction >= MAX_AUTO_RETRIES:
            print(f"Reached the limit of {MAX_AUTO_RETRIES} automatic corrections.")
            return 2

        print(f"\nC# compilation failed. Preparing correction "
              f"{correction + 1}/{MAX_AUTO_RETRIES}; the next diff still needs APPLY.")
        parent_run_id = identifier
        project = workspace
        task = repair_task(compiler_errors, objective)

    return 2


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--demo", action="store_true",
                        help="Use frozen C# compile-error fixture")
    parser.add_argument("--project", help="Path to local project directory")
    parser.add_argument("--retry", metavar="RUN_ID",
                        help="Use approved disposable MVP run, compile it, then ask Qwen to fix build errors")
    parser.add_argument("--file", help="One relative source file to edit")
    parser.add_argument("--task", help="Plain-English change request")
    parser.add_argument("--model", default="qwen3.5:4b")
    parser.add_argument("--build", action="store_true",
                        help="After approval, use isolated Podman for fixed dotnet build")
    parser.add_argument("--run", action="store_true",
                        help="After successful isolated build, run the approved C# copy inside Podman")
    args = parser.parse_args()
    if sum((bool(args.demo), bool(args.project), bool(args.retry))) != 1:
        parser.error("Choose --demo, --retry RUN_ID, or --project with --file and --task")
    if args.project and not (args.file and args.task):
        parser.error("For --project, supply --file and --task")
    if args.run and not (args.demo or args.retry):
        parser.error("For custom projects, --run is not yet offered; use --build")
    try:
        return run(args)
    except (OSError, ValueError, UnicodeError, urllib.error.URLError,
            subprocess.SubprocessError) as exc:
        print(f"BLOCKED: {type(exc).__name__}: {str(exc)[:180]}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
