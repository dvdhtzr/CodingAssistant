#!/usr/bin/env python3
"""One-command Dell Mission Control launcher. No cloud models or background service.

From the checkout: python3 tools/m3/launch.py

Uses an installed M3 build, a downloaded GitHub Actions ZIP, or the gh CLI
to retrieve a verified CI artifact. If necessary, opens the Actions artifact
page for a single manual browser download. The local owner API and frontend
run together; execution stays disabled unless --enable-execution is supplied.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path, PurePosixPath
import secrets
import shutil
import socket
import stat
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import webbrowser
import zipfile

ROOT = Path(__file__).resolve().parents[2]
REPO = "dvdhtzr/CodingAssistant"
ARTIFACT = "codingassistant-linux-x64"
# Last known cross-platform-green M3 PR build; CI artifacts expire after 7 days.
# The launcher prefers a newer successful GitHub Actions run via gh.
FALLBACK_RUN = 44726245411
ACTIONS = f"https://github.com/{REPO}/actions/runs/{FALLBACK_RUN}"
DESTINATION = Path.home() / ".local" / "opt" / "codingassistant-m3"
PORT = 5098


class LaunchError(RuntimeError):
    pass


def valid_app(path: Path) -> bool:
    binary = path / "CodingAssistant.Api"
    html = path / "wwwroot" / "index.html"
    return (binary.is_file() and not binary.is_symlink()
            and html.is_file() and not html.is_symlink()
            and "Mission Control" in html.read_text(encoding="utf-8", errors="replace"))


def safe_directory(directory: Path) -> None:
    """Reject symlinks and non-file members, including after gh extraction."""
    root = directory.resolve(strict=True)
    for path in directory.rglob("*"):
        if path.is_symlink():
            raise LaunchError("Refusing symlink in downloaded CI package")
        resolved = path.resolve(strict=True)
        if root not in resolved.parents and resolved != root:
            raise LaunchError("Refusing path outside the CI package")
        if not (path.is_dir() or path.is_file()):
            raise LaunchError("Unexpected special file in CI package")


def unpack_zip(archive: Path, directory: Path) -> None:
    if archive.is_symlink() or not archive.is_file():
        raise LaunchError("Artifact ZIP is not a regular file")
    with zipfile.ZipFile(archive) as zipped:
        entries = zipped.infolist()
        if len(entries) > 400 or sum(info.file_size for info in entries) > 700_000_000:
            raise LaunchError("Artifact ZIP exceeds safety limits")
        for info in entries:
            name = PurePosixPath(info.filename)
            mode = info.external_attr >> 16
            if (name.is_absolute() or ".." in name.parts or not name.parts or
                    chr(92) in info.filename or
                    (stat.S_IFMT(mode) not in (0, stat.S_IFREG, stat.S_IFDIR))):
                raise LaunchError("Refusing unsafe artifact ZIP member")
        zipped.extractall(directory)
    safe_directory(directory)


def gh_download(directory: Path) -> bool:
    gh = shutil.which("gh")
    if not gh:
        return False
    try:
        listed = subprocess.run(
            [gh, "run", "list", "--repo", REPO, "--workflow", "m0-tests.yml",
             "--limit", "30", "--json", "databaseId,conclusion,headBranch"],
            stdin=subprocess.DEVNULL, text=True, capture_output=True, timeout=25,
            check=False)
        runs = json.loads(listed.stdout) if listed.returncode == 0 else []
        ids = [r["databaseId"] for r in runs
               if r.get("conclusion") == "success"
               and (r.get("headBranch") == "main"
                    or r.get("databaseId") == FALLBACK_RUN)]
        if FALLBACK_RUN not in ids:
            ids.append(FALLBACK_RUN)
        for run_id in ids[:6]:
            fetched = subprocess.run(
                [gh, "run", "download", str(run_id), "--repo", REPO,
                 "--name", ARTIFACT, "--dir", str(directory)],
                stdin=subprocess.DEVNULL, text=True, capture_output=True,
                timeout=180, check=False)
            if fetched.returncode == 0 and valid_app(directory):
                safe_directory(directory)
                return True
            # Avoid keeping a partial/incorrect download on retry.
            for member in directory.iterdir():
                if member.is_dir():
                    shutil.rmtree(member)
                else:
                    member.unlink()
    except (OSError, ValueError, subprocess.TimeoutExpired):
        return False
    return False


def install_app(destination: Path = DESTINATION, *, interactive: bool = True) -> Path:
    if valid_app(destination):
        return destination
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".codingassistant-m3-", dir=destination.parent) as temp:
        staging = Path(temp)
        downloads = Path.home() / "Downloads"
        archives = sorted(downloads.glob(f"{ARTIFACT}*.zip"),
                          key=lambda p: p.stat().st_mtime if p.is_file() else 0,
                          reverse=True) if downloads.is_dir() else []
        if archives:
            print("Installing the downloaded Mission Control build...")
            unpack_zip(archives[0], staging)
        else:
            print("Finding a successful GitHub Actions Mission Control build...")
            if not gh_download(staging):
                if not interactive:
                    raise LaunchError("No local build or authenticated gh download available")
                print("\nOne-time download needed. Sign in to GitHub and click")
                print(f"the '{ARTIFACT}' artifact under this build:")
                print(ACTIONS)
                webbrowser.open(ACTIONS)
                input("\nSave the ZIP to ~/Downloads, then press Enter here: ")
                archives = sorted(downloads.glob(f"{ARTIFACT}*.zip"),
                                  key=lambda p: p.stat().st_mtime, reverse=True)
                if not archives:
                    raise LaunchError("No artifact ZIP found in ~/Downloads")
                unpack_zip(archives[0], staging)
        if not valid_app(staging):
            raise LaunchError("Downloaded build lacks the M3 API or React interface")
        safe_directory(staging)
        shutil.copytree(staging, destination, dirs_exist_ok=True)
        (destination / "CodingAssistant.Api").chmod(
            (destination / "CodingAssistant.Api").stat().st_mode | stat.S_IXUSR)
        if not valid_app(destination):
            raise LaunchError("Mission Control installation did not verify")
    return destination


def free_port(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as client:
        return client.connect_ex(("127.0.0.1", port)) != 0


def start(api_binary: Path, *, enable_execution: bool = False,
          open_browser: bool = True) -> None:
    if not free_port(PORT):
        raise LaunchError(f"Port {PORT} is already in use. Close the existing server first.")
    token = secrets.token_hex(32)
    env = os.environ.copy()
    env["CODINGASSISTANT_OWNER_TOKEN"] = token
    env["CODINGASSISTANT_PORT"] = str(PORT)
    env["CODINGASSISTANT_ENABLE_LOCAL_EXECUTION"] = "1" if enable_execution else "0"
    if enable_execution:
        env["CODINGASSISTANT_REPOSITORY_ROOT"] = str(ROOT)
    else:
        env.pop("CODINGASSISTANT_REPOSITORY_ROOT", None)

    process = subprocess.Popen(
        [str(api_binary)], cwd=ROOT, env=env,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        url = f"http://127.0.0.1:{PORT}/"
        for _ in range(80):
            if process.poll() is not None:
                raise LaunchError("Local API exited during startup; check the installed build.")
            try:
                with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(
                    url, timeout=1) as response:
                    if response.status == 200:
                        break
            except (urllib.error.URLError, TimeoutError):
                time.sleep(.15)
        else:
            raise LaunchError("Local API did not become ready")

        print("\nMISSION CONTROL IS READY")
        print("Browser:", url)
        print("Owner token (paste into the login screen):")
        print(token)
        print("\nToken is temporary and never saved. Closing this window stops the app.")
        if not enable_execution:
            print("AI coding is OFF for this first browser test.")
        else:
            print("Coding enabled ONLY for the approved disposable fixture.")
            print("The complete M1 execution hash still needs separate approval.")
        if open_browser:
            webbrowser.open(url)
        process.wait()
    except KeyboardInterrupt:
        print("\nStopping Mission Control...")
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--enable-execution", action="store_true",
                        help="Explicitly enable the still approval-gated M1 fixed fixture")
    parser.add_argument("--no-browser", action="store_true",
                        help="Do not open Firefox automatically")
    args = parser.parse_args()
    try:
        if not sys.platform.startswith("linux") or os.geteuid() == 0:
            raise LaunchError("Run as your normal, non-root Linux user")
        app = install_app()
        start(app / "CodingAssistant.Api", enable_execution=args.enable_execution,
              open_browser=not args.no_browser)
        return 0
    except (LaunchError, OSError, EOFError, zipfile.BadZipFile) as exc:
        print(f"Mission Control could not start: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
