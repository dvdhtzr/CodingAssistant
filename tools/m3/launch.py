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
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
from urllib.parse import urlsplit
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
ACTIONS = f"https://github.com/{REPO}/actions/workflows/m0-tests.yml?query=branch%3Amain"
DESTINATION = Path.home() / ".local" / "opt" / "codingassistant-m3"
PORT = 5098
RELEASE_API = f"https://api.github.com/repos/{REPO}/releases/latest"
RELEASE_FILE = ARTIFACT + ".zip"
RELEASE_SHA = RELEASE_FILE + ".sha256"
MAX_RELEASE_BYTES = 220_000_000


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


def gh_download(directory: Path, *, current_commit: bool = False) -> bool:
    gh = shutil.which("gh")
    if not gh:
        return False
    try:
        head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT,
                              text=True, capture_output=True, timeout=8,
                              check=False)
        if current_commit and (head.returncode != 0 or
                               len(head.stdout.strip()) != 40):
            return False
        listed = subprocess.run(
            [gh, "run", "list", "--repo", REPO, "--workflow", "m0-tests.yml",
             "--limit", "30", "--json", "databaseId,conclusion,headBranch,headSha"],
            stdin=subprocess.DEVNULL, text=True, capture_output=True, timeout=25,
            check=False)
        runs = json.loads(listed.stdout) if listed.returncode == 0 else []
        ids = [r["databaseId"] for r in runs
               if r.get("conclusion") == "success" and (
                   r.get("headSha") == head.stdout.strip()
                   if current_commit else r.get("headBranch") == "main")]
        if not current_commit and FALLBACK_RUN not in ids:
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


def checkout_commit() -> str | None:
    """Only a recognized 40-digit Git commit can be matched to a release."""
    try:
        result = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT,
                                text=True, capture_output=True, check=False,
                                stdin=subprocess.DEVNULL, timeout=8)
        sha = result.stdout.strip()
        return sha if result.returncode == 0 and len(sha) == 40 and all(
            ch in "0123456789abcdef" for ch in sha) else None
    except (OSError, subprocess.TimeoutExpired):
        return None


def official_https(url: str, *, exact_host: str | None = None) -> bool:
    value = urlsplit(url)
    return (value.scheme == "https" and not value.username and not value.password
            and not value.port and
            (value.hostname == exact_host if exact_host else
             value.hostname in {"api.github.com", "github.com",
                                "release-assets.githubusercontent.com",
                                "objects.githubusercontent.com"}))


def public_release() -> tuple[str, str, str] | None:
    """Return official tag-bound SHA, release ZIP URL and checksum URL.

    Network failure is an *offline cache hit*, never a reason to discard a
    verified installed executable. The publisher runs only after both CI OS
    jobs pass; release tag embeds the complete source commit.
    """
    try:
        request = urllib.request.Request(RELEASE_API, headers={
            "Accept": "application/vnd.github+json", "User-Agent": "CodingAssistant-M3",
        })
        with urllib.request.urlopen(request, timeout=12) as reply:
            if not official_https(reply.geturl(), exact_host="api.github.com"):
                raise LaunchError("Unexpected GitHub API redirect")
            body = reply.read(256_001)
        if len(body) > 256_000:
            raise LaunchError("Unexpectedly large GitHub release metadata")
        data = json.loads(body)
        tag = data.get("tag_name", "")
        if not (isinstance(tag, str) and tag.startswith("m3-") and
                len(tag) == 43 and all(ch in "0123456789abcdef" for ch in tag[3:]) and
                data.get("draft") is False and data.get("prerelease") is False):
            raise LaunchError("Official release has unexpected version metadata")
        assets = {asset.get("name"): asset for asset in data.get("assets", [])
                  if isinstance(asset, dict)}
        for filename in (RELEASE_FILE, RELEASE_SHA):
            asset = assets.get(filename)
            expected = f"https://github.com/{REPO}/releases/download/{tag}/{filename}"
            if (not isinstance(asset, dict) or
                asset.get("browser_download_url") != expected or
                not isinstance(asset.get("size"), int) or
                asset["size"] < 1 or asset["size"] > MAX_RELEASE_BYTES):
                raise LaunchError("Official release asset metadata is incomplete")
        return tag[3:], assets[RELEASE_FILE]["browser_download_url"], assets[RELEASE_SHA]["browser_download_url"]
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError):
        return None


def fetch_public_asset(url: str, destination: Path, size_limit: int) -> None:
    if not official_https(url, exact_host="github.com"):
        raise LaunchError("Refusing unofficial application download URL")
    request = urllib.request.Request(url, headers={"User-Agent": "CodingAssistant-M3"})
    with urllib.request.urlopen(request, timeout=90) as reply:
        if not official_https(reply.geturl()):
            raise LaunchError("Refusing non-GitHub download redirect")
        total = 0
        with destination.open("xb") as stream:
            while True:
                chunk = reply.read(65536)
                if not chunk:
                    break
                total += len(chunk)
                if total > size_limit:
                    raise LaunchError("Downloaded application exceeded safety limit")
                stream.write(chunk)
    if total == 0:
        raise LaunchError("Empty application download")


def install_release(release: tuple[str, str, str], staging: Path) -> str:
    sha, zip_url, sum_url = release
    with tempfile.TemporaryDirectory(prefix=".release-download-", dir=staging.parent) as temp:
        files = Path(temp)
        checksum = files / RELEASE_SHA
        archive = files / RELEASE_FILE
        fetch_public_asset(sum_url, checksum, 256)
        parts = checksum.read_text(encoding="ascii").strip().split()
        if (len(parts) != 2 or parts[1] != RELEASE_FILE or
            len(parts[0]) != 64 or
            any(ch not in "0123456789abcdef" for ch in parts[0])):
            raise LaunchError("Invalid official release checksum")
        fetch_public_asset(zip_url, archive, MAX_RELEASE_BYTES)
        if hashlib.sha256(archive.read_bytes()).hexdigest() != parts[0]:
            raise LaunchError("Official release ZIP checksum mismatch")
        unpack_zip(archive, staging)
    if not valid_app(staging):
        raise LaunchError("Official release is missing its application or UI")
    (staging / ".release-commit").write_text(sha + "\n", encoding="ascii")
    return sha


def install_app(destination: Path = DESTINATION, *, interactive: bool = True,
                refresh: bool = False) -> Path:
    """Prefer official public checked release, then existing offline installation.

    This is deliberately safe to repeat. No manual unzip, no need for 'gh'
    authentication, and no update ever replaces a running process.
    """
    if destination.is_symlink() or (destination.exists() and not destination.is_dir()):
        raise LaunchError("Refusing non-directory or symlinked installation target")
    cached = valid_app(destination)
    release = public_release()
    head = checkout_commit()
    if release and head == release[0]:
        existing = destination / ".release-commit"
        if cached and not refresh and existing.is_file() and existing.read_text(
            encoding="ascii").strip() == head:
            print("Mission Control is up to date.", flush=True)
            return destination
    else:
        if cached:
            print("Using installed Mission Control (update unavailable or still building).",
                  flush=True)
            return destination
        if release is not None and head != release[0]:
            print("Application release is not for the current source checkout.",
                  flush=True)
        release = None

    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".codingassistant-m3-", dir=destination.parent) as temp:
        staging = Path(temp) / "package"
        staging.mkdir()
        downloaded = False
        if release is not None:
            try:
                print("Installing verified official Mission Control release...", flush=True)
                install_release(release, staging)
                downloaded = True
            except (OSError, urllib.error.URLError, TimeoutError, LaunchError,
                    zipfile.BadZipFile, UnicodeDecodeError) as error:
                if cached:
                    print("Update unavailable; leaving existing application untouched:",
                          type(error).__name__, flush=True)
                    return destination
                raise LaunchError("Official release could not be verified") from error
        if not downloaded:
            # Compatibility with the original build before the first public
            # release lands on main. Cached installations require no fallback.
            archives = sorted((Path.home() / "Downloads").glob(f"{ARTIFACT}*.zip"),
                              key=lambda p: p.stat().st_mtime, reverse=True)
            if not refresh and archives:
                print("Installing previously downloaded CI build...")
                unpack_zip(archives[0], staging)
                downloaded = True
            else:
                print("Looking for an authenticated GitHub Actions artifact...", flush=True)
                downloaded = gh_download(staging, current_commit=refresh)
            if not downloaded:
                if not interactive:
                    raise LaunchError("No verified downloadable application is available")
                before = time.time()
                print("One-time fallback: open the latest successful MAIN Actions build")
                print("and download the codingassistant-linux-x64 ZIP:", ACTIONS)
                webbrowser.open(ACTIONS)
                input("Save the ZIP to Downloads, then press Enter here: ")
                archives = sorted((Path.home() / "Downloads").glob(f"{ARTIFACT}*.zip"),
                                  key=lambda p: p.stat().st_mtime, reverse=True)
                if refresh:
                    archives = [a for a in archives if a.stat().st_mtime >= before]
                if not archives:
                    raise LaunchError("No freshly downloaded CI application ZIP found")
                unpack_zip(archives[0], staging)
        if not valid_app(staging):
            raise LaunchError("Downloaded build lacks the API or Mission Control interface")
        safe_directory(staging)
        binary = staging / "CodingAssistant.Api"
        binary.chmod(binary.stat().st_mode | stat.S_IXUSR)
        # Fully verified staged install. Roll back on unsuccessful atomic swap.
        backup = destination.parent / (".codingassistant-m3-previous-" + secrets.token_hex(6))
        old_present = destination.exists()
        if old_present:
            destination.rename(backup)
        try:
            staging.rename(destination)
        except OSError:
            if old_present:
                backup.rename(destination)
            raise
        if old_present:
            shutil.rmtree(backup)
    return destination


def free_port(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as client:
        return client.connect_ex(("127.0.0.1", port)) != 0


def verify_execution_preflight() -> None:
    """Verify actual rootless boundaries and refresh pinned offline runtime.

    This runs the existing M2→M1 model-free containment/preview checks, NOT an
    agent or model. Fail closed before starting an execution-enabled API.
    A stale source fingerprint may require a local offline derivative rebuild.
    """
    checker = ROOT / "tools" / "m2" / "dell_acceptance.py"
    if not checker.is_file() or checker.is_symlink():
        raise LaunchError("Missing reviewed M2→M1 preflight driver")
    print("Checking rootless containment and preparing pinned offline runtime...", flush=True)
    try:
        result = subprocess.run(
            [sys.executable, str(checker), "--preflight-only"],
            cwd=ROOT, stdin=subprocess.DEVNULL, check=False,
            timeout=1300,
        )
    except subprocess.TimeoutExpired as exc:
        raise LaunchError("M1 preflight timed out; coding remains OFF") from exc
    if result.returncode != 0:
        raise LaunchError(
            "M1 security/runtime preflight failed; coding remains OFF. "
            "Do not bypass the check or start an unqualified agent."
        )
    print("M1 model-free preflight PASS. Exact hash authorization still required.", flush=True)


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
    parser.add_argument("--refresh", action="store_true",
                        help="Download the latest CI application after a UI update")
    args = parser.parse_args()
    try:
        if not sys.platform.startswith("linux") or os.geteuid() == 0:
            raise LaunchError("Run as your normal, non-root Linux user")
        if not free_port(PORT):
            raise LaunchError("Mission Control is already running; close its terminal before restarting.")
        if args.enable_execution:
            verify_execution_preflight()
        app = install_app(refresh=args.refresh)
        start(app / "CodingAssistant.Api", enable_execution=args.enable_execution,
              open_browser=not args.no_browser)
        return 0
    except (LaunchError, OSError, EOFError, zipfile.BadZipFile) as exc:
        print(f"Mission Control could not start: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
