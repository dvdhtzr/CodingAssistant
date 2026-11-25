#!/usr/bin/env python3
"""Read-only comparison against independently reviewed host image provenance.

This is a review aid, not an execution admission gate. It never starts a
container, builds, pulls, imports credentials or calls a model. A matching
manifest is not a qualification result or an owner execution approval.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import platform
import re
import stat
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
PROFILES = ("dell-linux", "windows-wsl2")
ROLES = ("official", "runtime", "acpx", "composed", "evaluator")
INPUTS = (
    "infra/runtime/Containerfile",
    "infra/m1/Containerfile.opencode-acpx",
    "infra/qualification/Containerfile.candidate",
    "benchmarks/fixtures/03-fullstack-feature/web/package.json",
    "benchmarks/acceptance/TaskSummary.acceptance.test.tsx",
)
VERSIONS = {
    "openclaw_commit": "e13cf15ece9bbd969b7362927c315362c90c97ff",
    "openclaw": "2026.11.5", "acpx": "0.21.0", "opencode": "2.3.0",
}
OFFICIAL = (
    "ghcr.io/openclaw/openclaw@sha256:"
    "97781490d6cd01cec8d1f0ad300ff8bedfaae18ec5b8c30666aaebc470a6309f"
)
HEX64 = re.compile(r"[0-9a-f]{64}")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def is_hash(value):
    return type(value) is str and HEX64.fullmatch(value) is not None


def strict_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate manifest key")
        result[key] = value
    return result


def exact_keys(value, keys):
    return type(value) is dict and set(value) == set(keys)


def read_regular(path, limit):
    info = path.lstat()
    require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1,
            "Expected an unlinked regular file")
    require(0 < info.st_size <= limit, "File size outside review limits")
    with path.open("rb") as stream:
        data = stream.read(limit + 1)
    require(0 < len(data) <= limit, "File size outside review limits")
    return data


def sha(data):
    return hashlib.sha256(data).hexdigest()


def parse_manifest(data, reviewed_hash, profile):
    require(is_hash(reviewed_hash) and sha(data) == reviewed_hash,
            "Manifest bytes do not match independently supplied review hash")
    item = json.loads(data, object_pairs_hook=strict_object)
    require(exact_keys(item, ("schema_version", "profile", "host_binding_sha256",
                              "versions", "image_ids", "recipe_inputs_sha256",
                              "official_repo_digest")),
            "Unexpected or missing manifest fields")
    require(type(item["schema_version"]) is int and item["schema_version"] == 1,
            "Unsupported manifest version")
    require(profile in PROFILES and item["profile"] == profile,
            "Host profile differs from requested reviewed profile")
    require(is_hash(item["host_binding_sha256"]), "Missing host binding")
    require(item["versions"] == VERSIONS, "Source or package pins differ")
    require(item["official_repo_digest"] == OFFICIAL, "Official manifest pin differs")
    ids = item["image_ids"]
    require(exact_keys(ids, ROLES) and all(is_hash(v) for v in ids.values()),
            "Every role requires a full immutable local image ID")
    require(len(set(ids.values())) == len(ROLES), "Image roles must not alias")
    inputs = item["recipe_inputs_sha256"]
    require(exact_keys(inputs, INPUTS) and all(is_hash(v) for v in inputs.values()),
            "Missing or invalid reviewed recipe input hash")
    return item


def host_binding(profile):
    """Local correlation, not remote attestation or proof of host ownership."""
    require(sys.platform == "linux" and os.geteuid() != 0,
            "Normal Linux/WSL user required")
    release = platform.release().lower()
    wsl = "microsoft" in release
    require((profile == "dell-linux" and not wsl) or
            (profile == "windows-wsl2" and wsl and "wsl2" in release),
            "Kernel does not match requested host profile")
    machine = read_regular(pathlib.Path("/etc/machine-id"), 128).strip()
    require(re.fullmatch(rb"[0-9a-f]{32}", machine) is not None,
            "Missing valid local machine identifier")
    # Never report the raw machine identifier or its derived binding.
    return sha(b"codingassistant-provenance-v1\0" + profile.encode() + b"\0" + machine)


def fixed(argv):
    return subprocess.run(argv, stdin=subprocess.DEVNULL,
                          capture_output=True, text=True, timeout=30, check=False)


def inspect_id(image_id, role):
    result = fixed(["podman", "image", "inspect", image_id])
    require(result.returncode == 0, "Exact local image missing: " + role)
    items = json.loads(result.stdout, object_pairs_hook=strict_object)
    require(type(items) is list and len(items) == 1 and type(items[0]) is dict,
            "Unexpected image inspection shape: " + role)
    meta = items[0]
    actual = meta.get("Id")
    require(type(actual) is str and actual in (image_id, "sha256:" + image_id),
            "Exact local image identity mismatch: " + role)
    if role == "official":
        digests = meta.get("RepoDigests")
        require(type(digests) is list and all(type(x) is str for x in digests)
                and OFFICIAL in digests, "Official registry manifest mismatch")


def verify(data, reviewed_hash, profile):
    manifest = parse_manifest(data, reviewed_hash, profile)
    require(host_binding(profile) == manifest["host_binding_sha256"],
            "Reviewed host binding mismatch")
    for relative, expected in manifest["recipe_inputs_sha256"].items():
        source = ROOT / relative
        require(not any(parent.is_symlink() for parent in source.parents
                        if parent != ROOT and ROOT in parent.parents),
                "Linked recipe parent")
        require(sha(read_regular(source, 2_000_000)) == expected,
                "Reviewed recipe input changed: " + relative)
    rootless = fixed(["podman", "info", "--format", "{{.Host.Security.Rootless}}"])
    require(rootless.returncode == 0 and rootless.stdout.strip() == "true",
            "Rootless Podman not confirmed")
    for role in ROLES:
        inspect_id(manifest["image_ids"][role], role)
    return {
        "status": "REVIEWED_INPUTS_MATCH_NOT_QUALIFIED",
        "profile": profile, "manifest_sha256": reviewed_hash,
        "matched_image_roles": list(ROLES), "matched_recipe_count": len(INPUTS),
        "execution_authorized": False, "runtime_qualified": False,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", choices=PROFILES, required=True)
    parser.add_argument("--manifest", type=pathlib.Path, required=True)
    parser.add_argument("--manifest-sha256", required=True)
    args = parser.parse_args()
    try:
        data = read_regular(args.manifest, 65536)
        report = verify(data, args.manifest_sha256, args.profile)
    except (ValueError, OSError, subprocess.TimeoutExpired, UnicodeError) as exc:
        print("BLOCKED:", type(exc).__name__, str(exc)[:300], file=sys.stderr)
        return 2
    print(json.dumps(report, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
