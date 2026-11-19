#!/usr/bin/env python3
"""M1.1 official OpenClaw image-only rootless Podman prerequisite probe.

By default: inspect ALREADY-LOCAL image and run ONLY two fixed --version
commands in an isolated, network-disabled, non-root, read-only container.
With explicit --prepare: pull the ONE official versioned GHCR image first.
Does NOT install host Node/OpenClaw, start a Gateway or ACP, create
provider config, mount home/workspaces, or perform model inference.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import pathlib
import re
import subprocess
import sys
from typing import Any

from discovery import node_compat

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "artifacts/m1/gateway-image-probe.json"
IMAGE = "ghcr.io/openclaw/openclaw:2026.11.5"
VERSION = "2026.11.5"
IMAGE_ID = re.compile(r"(?:sha256:)?([a-f0-9]{64})\Z")
REPO_DIGEST = re.compile(
    r"ghcr\.io/openclaw/openclaw@sha256:[a-f0-9]{64}\Z")
SEMVER = re.compile(r"(?<![\d.])v?(\d+)\.(\d+)\.(\d+)(?![\d.])")


def fixed(argv: list[str], timeout: int = 50) -> subprocess.CompletedProcess[str]:
    return subprocess.run(argv, stdin=subprocess.DEVNULL,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          text=True, encoding="utf-8", errors="replace",
                          timeout=timeout, check=False)


def guest_command(image_id: str, uid: int, gid: int,
                  version_args: tuple[str, ...]) -> list[str]:
    if not IMAGE_ID.fullmatch(image_id):
        raise ValueError("Immutable image ID required")
    if version_args not in (("--version",),
                            ("/app/openclaw.mjs", "--version")):
        raise ValueError("Only exact version probes authorized")
    return [
        "podman", "run", "--rm", "--pull=never", "--network=none",
        "--read-only", "--cap-drop=all",
        "--security-opt=no-new-privileges", "--userns=keep-id",
        f"--user={uid}:{gid}", "--pids-limit=64", "--memory=2g",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=128m,notmpcopyup",
        "--env=HOME=/tmp", "--env=XDG_CONFIG_HOME=/tmp/.config",
        "--workdir=/app", "--entrypoint=node",
        image_id, *version_args,
    ]


def inspected_image() -> dict[str, str] | None:
    result = fixed(["podman", "image", "inspect", IMAGE])
    if result.returncode != 0:
        return None
    items = json.loads(result.stdout)
    if type(items) is not list or len(items) != 1 or type(items[0]) is not dict:
        raise ValueError("Image inspect response shape changed")
    info = items[0]
    raw_id = info.get("Id")
    if type(raw_id) is not str or not IMAGE_ID.fullmatch(raw_id):
        raise ValueError("Inspect did not return immutable image ID")
    sha = IMAGE_ID.fullmatch(raw_id).group(1)
    digests = info.get("RepoDigests")
    if type(digests) is not list:
        raise ValueError("Official image provenance missing RepoDigests")
    matches = [item for item in digests
               if type(item) is str and REPO_DIGEST.fullmatch(item)]
    if not matches:
        raise ValueError("Image lacks official GHCR RepoDigest")
    return {"image_id": sha, "repo_digest": sorted(matches)[0]}


def version_text(output: str) -> str | None:
    # Never persist raw process output or read package/config files.
    found = SEMVER.search(output[:1200])
    return found.group(0) if found else None


def probe(prepare: bool = False) -> dict[str, Any]:
    report: dict[str, Any] = {
        "schema_version": 1, "stage": "M1.1-official-isolated-gateway-image",
        "recorded_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "BLOCKED", "official_image": IMAGE,
        "pull_requested": prepare, "image_id": None, "repo_digest": None,
        "node_version": None, "openclaw_version": None,
        "gateway_started": False, "acp_session_started": False,
        "model_inference": False, "host_node_updated": False,
        "host_openclaw_installed": False,
        "source_modified": False,
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        report["reason"] = "Requires Linux non-root rootless Podman"
        return report
    try:
        rootless = fixed(["podman", "info", "--format",
                          "{{.Host.Security.Rootless}}"], timeout=25)
        if rootless.returncode != 0 or rootless.stdout.strip() != "true":
            raise ValueError("Rootless Podman not confirmed")
        if prepare:
            pulled = fixed(["podman", "pull", IMAGE], timeout=900)
            if pulled.returncode != 0:
                raise ValueError("Official GHCR image pull failed")
        known = inspected_image()
        if known is None:
            report["reason"] = "IMAGE_NOT_PRESENT: rerun with explicit --prepare"
            return report
        report.update(known)
        uid, gid = os.getuid(), os.getgid()
        for key, args in (
            ("node_version", ("--version",)),
            ("openclaw_version", ("/app/openclaw.mjs", "--version")),
        ):
            res = fixed(guest_command(known["image_id"], uid, gid, args),
                        timeout=120)
            parsed = version_text(res.stdout) if res.returncode == 0 else None
            if not parsed:
                raise ValueError(key + " unavailable within isolated image")
            report[key] = parsed
        patched = node_compat(report["node_version"])
        if not patched["patched_recommended"]:
            raise ValueError("Gateway image Node lacks recommended security patches")
        if tuple(map(int, SEMVER.search(report["openclaw_version"]).groups())) != (
                2026, 11, 5):
            raise ValueError("OpenClaw CLI version differs from pinned GHCR tag")
        report["status"] = "RUNTIME_IMAGE_PASS"
    except (ValueError, OSError, subprocess.TimeoutExpired,
            TypeError, KeyError, json.JSONDecodeError) as exc:
        report["reason"] = str(exc)[:180]
    return report


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--prepare", action="store_true",
                    help="Explicitly download pinned official image into rootless Podman store")
    args = ap.parse_args()
    data = probe(prepare=args.prepare)
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(json.dumps(data, indent=2, sort_keys=True) + "\n",
                      encoding="utf-8")
    print("M1.1", data["status"])
    print("  official image:", IMAGE)
    print("  immutable image ID:", data["image_id"] or "NOT_AVAILABLE")
    print("  verified registry digest:", data["repo_digest"] or "NOT_AVAILABLE")
    print("  isolated Node:", data["node_version"] or "NOT_AVAILABLE")
    print("  isolated OpenClaw:", data["openclaw_version"] or "NOT_AVAILABLE")
    print("  Gateway/ACP/model executed: NO")
    print("  Host Node/OpenClaw/source changed: NO")
    if data.get("reason"):
        print("  blocker:", data["reason"])
    print("Evidence:", OUTPUT)
    return 0 if data["status"] == "RUNTIME_IMAGE_PASS" else 2


if __name__ == "__main__":
    sys.exit(main())
