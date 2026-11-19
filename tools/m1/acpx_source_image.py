#!/usr/bin/env python3
"""M1.4: official-source-pinned, opt-in local OpenClaw+ACPX image build.

Default: inspect only a previously built local image in sealed offline guests.
--prepare: explicitly clone verified official Git tag, build the official
Dockerfile with OPENCLAW_EXTENSIONS=acpx, then inspect offline. This executes
upstream build scripts and downloads public build dependencies (supply-chain
risk), but runs NO Gateway/ACP agent/model, changes no host Node/npm installation
and mounts no personal files into any runtime container.

No secrets, credentials, unrelated repos or host config are copied into the
build context; all build input is the verified upstream source checkout.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import pathlib
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import uuid

from acpx_image_inventory import SOURCE as INVENTORY_JS, parse_report
from discovery import node_compat
from gateway_image_probe import IMAGE_ID, inspected_image, version_text
from gateway_loopback_probe import APPROVED_IMAGE_ID, APPROVED_REPO_DIGEST

ROOT = pathlib.Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts/m1"
REPO_URL = "https://github.com/openclaw/openclaw.git"
SOURCE_TAG = "v2026.11.5"
SOURCE_COMMIT = "e13cf15ece9bbd969b7362927c315362c90c97ff"
IMAGE_TAG = "localhost/codingassistant-openclaw-acpx:2026.11.5-e13cf15e"
PLUGIN_VERSION = "2026.11.5"
ACPX_VERSION = "0.21.0"
VERSIONED_LABELS = {
    "org.opencontainers.image.source": "https://github.com/openclaw/openclaw",
    "org.opencontainers.image.revision": SOURCE_COMMIT,
    "org.opencontainers.image.version": PLUGIN_VERSION,
    "io.github.dvdhtzr.codingassistant.selected-plugin": "acpx",
}
GUEST_BASE = (
    "podman", "run", "--rm", "--pull=never", "--network=none", "--read-only",
    "--cap-drop=all", "--security-opt=no-new-privileges", "--userns=keep-id",
)
HEX40 = re.compile(r"[0-9a-f]{40}\Z")


def sanitized_env(*, git: bool = False) -> dict[str, str]:
    allowed = {
        "PATH", "HOME", "USER", "LOGNAME", "LANG", "LC_ALL",
        "XDG_RUNTIME_DIR", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "TMPDIR",
        "DBUS_SESSION_BUS_ADDRESS", "CONTAINERS_STORAGE_CONF",
    }
    data = {key: value for key, value in os.environ.items() if key in allowed}
    # Prevent a public-source fetch from prompting for credentials or reading
    # user git config/hooks. GitHub authentication is never needed.
    if git:
        data["GIT_TERMINAL_PROMPT"] = "0"
        data["GIT_CONFIG_NOSYSTEM"] = "1"
        data["GIT_CONFIG_GLOBAL"] = os.devnull
        data["GIT_LFS_SKIP_SMUDGE"] = "1"
    return data


def command(argv: list[str], *, timeout: int = 60, log: pathlib.Path | None = None,
            git: bool = False) -> subprocess.CompletedProcess[str]:
    env = sanitized_env(git=git)
    if log is not None:
        flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
        fd = os.open(log, flags, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            outcome = subprocess.run(argv, stdin=subprocess.DEVNULL,
                                     stdout=stream, stderr=subprocess.STDOUT,
                                     env=env, timeout=timeout, check=False)
        return subprocess.CompletedProcess(argv, outcome.returncode, "", "")
    return subprocess.run(argv, stdin=subprocess.DEVNULL,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          text=True, encoding="utf-8", errors="replace",
                          env=env, timeout=timeout, check=False)


def verify_prerequisites() -> None:
    if sys.platform != "linux" or os.geteuid() == 0:
        raise ValueError("Linux rootless non-root Podman required")
    rootless = command(["podman", "info", "--format",
                        "{{.Host.Security.Rootless}}"], timeout=25)
    if rootless.returncode != 0 or rootless.stdout.strip() != "true":
        raise ValueError("Podman rootless preflight failed")
    known = inspected_image()
    if known is None or known["image_id"] != APPROVED_IMAGE_ID or (
            known["repo_digest"] != APPROVED_REPO_DIGEST):
        raise ValueError("Original M1.1 official image identity changed")


def clone_command(destination: pathlib.Path) -> list[str]:
    return [
        "git", "-c", "credential.helper=", "-c", "core.hooksPath=/dev/null",
        "clone", "--depth=1", "--single-branch", "--branch", SOURCE_TAG,
        "--no-recurse-submodules", REPO_URL, str(destination),
    ]


def source_pin_command(path: pathlib.Path) -> list[str]:
    return ["git", "-C", str(path), "rev-parse", "HEAD"]


def verify_source(path: pathlib.Path) -> None:
    result = command(source_pin_command(path), timeout=20, git=True)
    if result.returncode != 0 or result.stdout.strip() != SOURCE_COMMIT:
        raise ValueError("Official source tag changed / unexpected Git commit")
    result = command(["git", "-C", str(path), "status", "--porcelain",
                      "--untracked-files=normal"], timeout=20, git=True)
    if result.returncode != 0 or result.stdout.strip():
        raise ValueError("Unclean official-source build context")
    for name in ("Dockerfile", "pnpm-lock.yaml", "extensions/acpx/package.json"):
        entry = path / name
        if (not entry.is_file() or entry.is_symlink() or
                not stat.S_ISREG(entry.stat().st_mode)):
            raise ValueError("Missing regular upstream build source: " + name)
    manifest = json.loads((path / "extensions/acpx/package.json").read_text())
    if (manifest.get("name") != "@openclaw/acpx" or
            manifest.get("version") != PLUGIN_VERSION or
            manifest.get("dependencies", {}).get("acpx") != ACPX_VERSION):
        raise ValueError("Unexpected official ACPX package/dependency versions")


def build_command(path: pathlib.Path) -> list[str]:
    argv = [
        "podman", "build", "--pull=missing", "--jobs=1",
        "--file", str(path / "Dockerfile"),
        "--tag", IMAGE_TAG,
        "--build-arg", "OPENCLAW_EXTENSIONS=acpx",
        "--build-arg", "OPENCLAW_DOCKER_BUILD_NODE_OPTIONS=--max-old-space-size=6144",
        "--build-arg", "OPENCLAW_DOCKER_BUILD_TSDOWN_MAX_OLD_SPACE_MB=6144",
    ]
    for key, value in sorted(VERSIONED_LABELS.items()):
        argv += ["--label", key + "=" + value]
    return [*argv, str(path)]


def inspect_derived() -> str | None:
    outcome = command(["podman", "image", "inspect", IMAGE_TAG], timeout=25)
    if outcome.returncode != 0:
        return None
    items = json.loads(outcome.stdout)
    if type(items) is not list or len(items) != 1 or type(items[0]) is not dict:
        raise ValueError("Unexpected local image inspect shape")
    meta = items[0]
    image_id = meta.get("Id")
    if type(image_id) is not str or not IMAGE_ID.fullmatch(image_id):
        raise ValueError("No immutable derived-image ID")
    image_id = IMAGE_ID.fullmatch(image_id).group(1)
    if image_id == APPROVED_IMAGE_ID:
        raise ValueError("Derived image unexpectedly aliases unmodified official image")
    labels = meta.get("Labels")
    if type(labels) is not dict or any(labels.get(key) != value
                                       for key, value in VERSIONED_LABELS.items()):
        raise ValueError("Pinned source/ACPX selection image labels missing/mismatched")
    if IMAGE_TAG not in meta.get("RepoTags", []):
        raise ValueError("Local image tag not associated with inspected image")
    return image_id


def offline_guest_command(image_id: str, args: tuple[str, ...]) -> list[str]:
    if not IMAGE_ID.fullmatch(image_id) or (
            args not in (("--version",),
                         ("/app/openclaw.mjs", "--version"),
                         ("-e", INVENTORY_JS))):
        raise ValueError("Offline derived-image guest request not allowed")
    uid, gid = os.getuid(), os.getgid()
    if uid <= 0 or gid <= 0:
        raise ValueError("Non-root guest required")
    return [
        *GUEST_BASE, f"--user={uid}:{gid}",
        "--pids-limit=64", "--memory=2g",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=128m,notmpcopyup",
        "--env=HOME=/tmp", "--env=XDG_CONFIG_HOME=/tmp/config",
        "--workdir=/app", "--entrypoint=node", image_id, *args,
    ]


def verify_offline(image_id: str) -> dict:
    results: dict[str, object] = {}
    for key, args in (
        ("node_version", ("--version",)),
        ("gateway_cli_version", ("/app/openclaw.mjs", "--version")),
        ("acpx_metadata", ("-e", INVENTORY_JS)),
    ):
        outcome = command(offline_guest_command(image_id, args), timeout=120)
        if outcome.returncode != 0:
            raise ValueError("Offline guest version/metadata probe failed: " + key)
        results[key] = (parse_report(outcome.stdout) if key == "acpx_metadata"
                        else version_text(outcome.stdout))
    if not results["node_version"] or not node_compat(
            str(results["node_version"]))["patched_recommended"]:
        raise ValueError("Derived image Node version does not meet security patch floor")
    if results["gateway_cli_version"] != PLUGIN_VERSION:
        raise ValueError("Derived Gateway CLI version differs from pinned source")
    data = results["acpx_metadata"]
    flags = data["present"]
    if not (data["manifest"] == "acpx" and flags["entry"] and
            data["pluginPackageVersion"] == PLUGIN_VERSION and
            (data["acpxDependencyRoot"] == ACPX_VERSION or
             data["acpxDependencyPlugin"] == ACPX_VERSION)):
        raise ValueError("Derived image ACPX runtime files/dependencies absent/incomplete")
    return results


def build_and_check(prepare: bool = False) -> dict:
    result = {
        "schema_version": 1, "stage": "M1.4-acpx-pinned-source-image",
        "recorded_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "BLOCKED", "official_git_tag": SOURCE_TAG,
        "source_commit": SOURCE_COMMIT, "local_tag": IMAGE_TAG,
        "prepare_requested": prepare, "source_verified": False,
        "image_id": None, "plugin_packaged": False,
        "offline_verification": None,
        "gateway_started": False, "acp_session_started": False,
        "model_inference": False, "host_node_modified": False,
        "base_official_image_modified": False, "host_source_modified": False,
        "reason": None, "build_log": None,
    }
    try:
        verify_prerequisites()
        if prepare:
            # Official source plus build tool dependencies require an explicit
            # download. Runtime verification below requires no network.
            if shutil.disk_usage(ROOT).free < 20 * 1024**3:
                raise ValueError("Insufficient free disk space (20 GiB required)")
            ARTIFACTS.mkdir(parents=True, exist_ok=True)
            with tempfile.TemporaryDirectory(prefix="ca-m14-source-",
                                             dir=ARTIFACTS) as folder:
                src = pathlib.Path(folder) / "openclaw"
                clone_log = ARTIFACTS / (
                    "acpx-clone-" + uuid.uuid4().hex[:12] + ".log")
                cloned = command(clone_command(src), git=True, timeout=480,
                                 log=clone_log)
                if cloned.returncode != 0:
                    raise ValueError("Pinned upstream Git clone failed; see " +
                                     clone_log.name)
                verify_source(src)
                result["source_verified"] = True
                build_log = ARTIFACTS / (
                    "acpx-build-" + uuid.uuid4().hex[:12] + ".log")
                result["build_log"] = str(build_log)
                built = command(build_command(src), timeout=5400, log=build_log)
                if built.returncode != 0:
                    raise ValueError("Rootless source build failed; see " +
                                     build_log.name)
        image_id = inspect_derived()
        if not image_id:
            raise ValueError("Derived image absent. Use --prepare for pinned build")
        result["image_id"] = image_id
        result["offline_verification"] = verify_offline(image_id)
        result["plugin_packaged"] = True
        result["status"] = "ACPX_IMAGE_PACKAGED_PASS"
    except (ValueError, OSError, KeyError, TypeError,
            json.JSONDecodeError, subprocess.TimeoutExpired) as exc:
        result["reason"] = str(exc)[:220]
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare", action="store_true",
                        help="Opt in to public Git clone and rootless Dockerfile build")
    args = parser.parse_args()
    result = build_and_check(prepare=args.prepare)
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    record = ARTIFACTS / (
        "acpx-image-" + uuid.uuid4().hex[:12] + ".json")
    fd = os.open(record, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as stream:
        json.dump(result, stream, sort_keys=True, indent=2)
        stream.write("\n")
    print("M1.4", result["status"])
    print("  source:", SOURCE_TAG, SOURCE_COMMIT)
    print("  plugin selected: acpx")
    print("  built image:", result["image_id"] or "NOT_VERIFIED")
    print("  packaged ACPX:", result["plugin_packaged"])
    print("  Gateway/ACP/Ollama executed: NO")
    print("  host Node/official Gateway image changed: NO")
    if result["build_log"]:
        print("  local build log:", result["build_log"])
    if result["reason"]:
        print("  blocker:", result["reason"])
    print("Evidence:", record)
    return 0 if result["status"] == "ACPX_IMAGE_PACKAGED_PASS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
