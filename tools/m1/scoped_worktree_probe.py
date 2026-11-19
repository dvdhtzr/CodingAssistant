#!/usr/bin/env python3
"""M1.10: real scoped writable fixture, OS denials, and independent host audit.

This test mounts exactly ONE freshly generated disposable project into the
owner-pinned combined OpenClaw/ACPX/OpenCode image; it never mounts a real
source repo, .git, user home, or provider credentials. Run a fixed Node and
child-Node workload, verify all in-workspace edits occurred, and separately
verify a neighboring unmounted sentinel was unchanged.

No Gateway, ACP session, Ollama call, Git remote or AI agent is invoked.
This does NOT qualify writable real-project execution or approval policy.
"""
from __future__ import annotations

import datetime as dt
import hashlib
import json
import os
import pathlib
import re
import secrets
import subprocess
import sys
import tempfile
import uuid

from acpx_initialize_probe import OWNER_IMAGE_ID
from acpx_opencode_image_probe import inspect_composed, verify_combined, verify_inputs
from gateway_image_probe import fixed

ROOT = pathlib.Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts/m1"
NAME = re.compile(r"ca-m110-scope-[0-9a-f]{12}\Z")

APP_BEFORE = "namespace Disposable;\npublic static class App { public const int Value = 1; }\n"
APP_AFTER = "namespace Disposable;\npublic static class App { public const int Value = 2; }\n"
FEATURE = "namespace Disposable;\npublic static class Feature { }\n"
CHILD = "namespace Disposable;\npublic static class Child { }\n"
UNCHANGED = "M1.10 FIXTURE NOTES. DO NOT ALTER.\n"
OUTSIDE_SENTINEL = "UNMOUNTED SIBLING: THIS MUST REMAIN UNCHANGED.\n"
CASES = (
    "approved_rewrite_succeeded",
    "approved_new_file_succeeded",
    "child_approved_write_succeeded",
    "escape_symlink_system_write_denied",
    "traversal_sibling_read_denied",
    "traversal_sibling_write_denied",
    "unmounted_project_unavailable",
    "git_metadata_not_mounted",
    "system_file_write_denied",
    "child_unapproved_write_denied",
)

# No host address, network socket, cloud model, git remote, or unknown
# input enters this fixed guest program. The sole bind is the temp fixture.
WORKLOAD_JS = r"""
const fs = require('node:fs');
const cp = require('node:child_process');
const result = {};
function denied(fn) {
  try { fn(); return false; } catch { return true; }
}
if (process.cwd() !== '/workspace') process.exit(2);
const app = '/workspace/App.cs';
const expectedBefore = 'namespace Disposable;\npublic static class App { public const int Value = 1; }\n';
const expectedAfter = 'namespace Disposable;\npublic static class App { public const int Value = 2; }\n';
const feature = 'namespace Disposable;\npublic static class Feature { }\n';
const childFile = 'namespace Disposable;\npublic static class Child { }\n';
if (fs.readFileSync(app,'utf8') !== expectedBefore) process.exit(2);
fs.writeFileSync(app, expectedAfter, 'utf8');
result.approved_rewrite_succeeded = fs.readFileSync(app,'utf8') === expectedAfter;
fs.writeFileSync('/workspace/Feature.cs',feature,{flag:'wx'});
result.approved_new_file_succeeded =
  fs.readFileSync('/workspace/Feature.cs','utf8') === feature;
const childCode = [
  "require('node:fs').writeFileSync('/workspace/Child.cs',",
  JSON.stringify(childFile),
  ",{flag:'wx'});process.stdout.write('OK');"
].join('');
const child = cp.spawnSync(process.execPath, ['-e', childCode],{
  encoding:'utf8', timeout:4000,maxBuffer:1024,env:{
    PATH:process.env.PATH,HOME:'/tmp',XDG_CONFIG_HOME:'/tmp/config'
  }
});
result.child_approved_write_succeeded =
  child.status === 0 && child.stdout === 'OK' &&
  fs.readFileSync('/workspace/Child.cs','utf8') === childFile;
fs.symlinkSync('/etc/passwd','/workspace/escape');
result.escape_symlink_system_write_denied =
  denied(() => fs.writeFileSync('/workspace/escape','MALICIOUS'));
result.traversal_sibling_read_denied =
  denied(() => fs.readFileSync('/workspace/../unapproved/keep.txt'));
result.traversal_sibling_write_denied =
  denied(() => fs.writeFileSync('/workspace/../unapproved/keep.txt','MALICIOUS'));
result.unmounted_project_unavailable =
  !fs.existsSync('/home/dave/CodingAssistant');
result.git_metadata_not_mounted =
  !fs.existsSync('/workspace/.git');
result.system_file_write_denied =
  denied(() => fs.writeFileSync('/etc/ca-m110-escape','MALICIOUS'));
const denialChild = cp.spawnSync(process.execPath, ['-e',
  "try{require('node:fs').writeFileSync('/etc/ca-m110-child','MALICIOUS');"+
  "process.stdout.write('BAD')}catch{process.stdout.write('DENIED')}"
],{
  encoding:'utf8', timeout:4000,maxBuffer:1024,
  env:{PATH:process.env.PATH,HOME:'/tmp'}
});
result.child_unapproved_write_denied =
  denialChild.status === 0 && denialChild.stdout === 'DENIED';
process.stdout.write(JSON.stringify(result)+'\n');
"""


def hash_file(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def fixture() -> tempfile.TemporaryDirectory[str]:
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    return tempfile.TemporaryDirectory(prefix="ca-m110-", dir=ARTIFACTS)


def setup(root: pathlib.Path) -> tuple[pathlib.Path, pathlib.Path]:
    approved = root / "approved"
    outside = root / "unapproved"
    approved.mkdir(mode=0o700)
    outside.mkdir(mode=0o700)
    (approved / "App.cs").write_text(APP_BEFORE, encoding="utf-8")
    (approved / "Notes.txt").write_text(UNCHANGED, encoding="utf-8")
    sentinel = outside / "keep.txt"
    sentinel.write_text(OUTSIDE_SENTINEL, encoding="utf-8")
    return approved, sentinel


def command_for(image_id: str, name: str, uid: int, gid: int,
                approved: pathlib.Path) -> list[str]:
    if image_id != OWNER_IMAGE_ID or not NAME.fullmatch(name):
        raise ValueError("Wrong immutable image or disposable guest name")
    if uid <= 0 or gid <= 0:
        raise ValueError("Root user forbidden")
    expected_parent = ARTIFACTS.resolve(strict=True)
    path = approved.resolve(strict=True)
    # Never accept any file outside our one-use generated fixture.
    if (path.name != "approved" or not path.is_dir() or
            path.is_symlink() or path.parent.parent != expected_parent or
            not re.fullmatch(r"ca-m110-[a-zA-Z0-9_-]+", path.parent.name) or
            not (path / "App.cs").is_file() or
            not (path / "Notes.txt").is_file() or
            any(p.is_symlink() for p in path.iterdir()) or
            (path / ".git").exists()):
        raise ValueError("No approved disposable non-git fixture at expected path")
    return [
        "podman", "run", "--rm", "--pull=never", "--name", name,
        "--network=none", "--read-only", "--cap-drop=all",
        "--security-opt=no-new-privileges", "--userns=keep-id",
        f"--user={uid}:{gid}", "--pids-limit=96", "--memory=2g",
        "--cpus=2",
        # This is the ONLY host bind. Mount a fresh disposable fixture,
        # never a real repo or its Git metadata.
        "--volume", str(path) + ":/workspace:rw,nosuid,nodev",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=256m,notmpcopyup",
        "--env=HOME=/tmp", "--env=XDG_CONFIG_HOME=/tmp/config",
        "--env=OPENCODE_DISABLE_AUTOUPDATE=1",
        "--workdir=/workspace", "--entrypoint=node",
        image_id, "-e", WORKLOAD_JS,
    ]


def parse(raw: str) -> dict[str, bool]:
    if not 0 < len(raw) < 2000:
        raise ValueError("Malformed or oversized guest output")
    values = json.loads(raw)
    if type(values) is not dict or set(values) != set(CASES):
        raise ValueError("Expected exactly ten negative/positive guest observations")
    if any(type(values[key]) is not bool for key in CASES):
        raise ValueError("Guest observation has wrong type")
    if not all(values.values()):
        raise ValueError("Worktree access policy violated: " + ", ".join(
            key for key in CASES if not values[key]))
    return {key: True for key in CASES}


def verify_host(approved: pathlib.Path, sentinel: pathlib.Path,
                original_sentinel_sha: str) -> dict[str, bool]:
    # This verification is performed by the trusted Python control plane,
    # independently of what the untrusted guest claimed it did.
    files = {p.name for p in approved.iterdir()}
    expected = {"App.cs", "Notes.txt", "Feature.cs", "Child.cs", "escape"}
    checks = {
        "only_expected_fixture_entries": files == expected,
        "authorized_file_change": (approved / "App.cs").read_text(
            encoding="utf-8") == APP_AFTER,
        "new_authorized_feature": (approved / "Feature.cs").read_text(
            encoding="utf-8") == FEATURE,
        "child_authorized_feature": (approved / "Child.cs").read_text(
            encoding="utf-8") == CHILD,
        "unchanged_fixture_notes": (approved / "Notes.txt").read_text(
            encoding="utf-8") == UNCHANGED,
        "external_sentinel_unchanged": hash_file(sentinel) == original_sentinel_sha,
        "external_sentinel_content": sentinel.read_text(
            encoding="utf-8") == OUTSIDE_SENTINEL,
        "symlink_stayed_inside_fixture": (
            (approved / "escape").is_symlink() and
            os.readlink(approved / "escape") == "/etc/passwd"),
        "git_metadata_never_present": not (approved / ".git").exists(),
    }
    if not all(checks.values()):
        raise ValueError("Independent host-side fixture/sentinel audit failed")
    return checks


def run() -> dict:
    report = {
        "schema_version": 1, "stage": "M1.10-disposable-scoped-worktree",
        "recorded_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "BLOCKED", "owner_image_id": None,
        "preflight_verified": False, "guest_checks": None,
        "host_audit": None, "guest_removed": False,
        "temporary_fixture_removed": False,
        "no_network": True, "only_disposable_fixture_mounted": True,
        "read_only_root_except_fixture": True,
        "git_metadata_mounted": False, "gateway_started": False,
        "acp_session": False, "model_inference": False,
        "actual_user_worktree_touched": False, "reason": None,
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        report["reason"] = "Linux rootless non-root Podman required"
        return report
    name = "ca-m110-scope-" + secrets.token_hex(6)
    try:
        m0_id = verify_inputs()
        image_id = inspect_composed(m0_id)
        if image_id != OWNER_IMAGE_ID:
            raise ValueError("Owner M1.7 immutable source image changed")
        report["owner_image_id"] = image_id
        packaged = verify_combined(image_id)
        if packaged["registry"] != {
                "id": "opencode", "installation": "installed",
                "expectedCommand": True}:
            raise ValueError("Pinned source image no longer has OpenCode ACP")
        report["preflight_verified"] = True
        with fixture() as directory:
            root = pathlib.Path(directory)
            approved, sentinel = setup(root)
            sentinel_sha = hash_file(sentinel)
            try:
                guest = fixed(command_for(image_id, name, os.getuid(),
                                          os.getgid(), approved), timeout=60)
                if guest.returncode != 0:
                    raise ValueError("Scoped guest failed (no raw guest logs saved)")
                report["guest_checks"] = parse(guest.stdout)
                report["host_audit"] = verify_host(
                    approved, sentinel, sentinel_sha)
                report["status"] = "SCOPED_WORKTREE_PASS"
            finally:
                # Must remove container BEFORE deleting fixture, even after
                # unsuccessful guest code; never leave a mounted directory.
                fixed(["podman", "rm", "-f", name], timeout=25)
                exists = fixed(["podman", "container", "exists", name], timeout=15)
                report["guest_removed"] = exists.returncode == 1
                if not report["guest_removed"]:
                    raise ValueError("Guest cleanup not confirmed; disposable fixture at " +
                                     root.name)
        report["temporary_fixture_removed"] = not root.exists()
    except (ValueError, KeyError, TypeError, OSError,
            json.JSONDecodeError, subprocess.TimeoutExpired) as exc:
        report["status"] = "BLOCKED"
        report["reason"] = str(exc)[:230]
    finally:
        if not report["guest_removed"]:
            try:
                fixed(["podman", "rm", "-f", name], timeout=25)
                exists = fixed(["podman", "container", "exists", name], timeout=15)
                report["guest_removed"] = exists.returncode == 1
            except (OSError, subprocess.TimeoutExpired):
                pass
        if not report["guest_removed"] or not report["temporary_fixture_removed"]:
            report["status"] = "BLOCKED"
            report["reason"] = report["reason"] or "Guest or fixture cleanup not verified"
    return report


def main() -> int:
    result = run()
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    path = ARTIFACTS / ("scoped-worktree-" + uuid.uuid4().hex[:12] + ".json")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as file:
        json.dump(result, file, indent=2, sort_keys=True)
        file.write("\n")
    print("M1.10", result["status"])
    print("  pinned image:", result["owner_image_id"] or "NOT_VERIFIED")
    print("  guest checks:", len(result["guest_checks"] or {}), "/", len(CASES))
    print("  independent host-side fixture checks:",
          len(result["host_audit"] or {}), "/ 9")
    print("  disposable guest removed:", result["guest_removed"])
    print("  temporary fixture removed:", result["temporary_fixture_removed"])
    print("  real worktree / Git metadata / Gateway / model: NOT_ATTEMPTED")
    if result["reason"]:
        print("  blocker:", result["reason"])
    print("Evidence:", path)
    return 0 if result["status"] == "SCOPED_WORKTREE_PASS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
