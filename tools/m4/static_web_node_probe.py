#!/usr/bin/env python3
"""Phase 4A fixed-fixture Node syntax isolation probe.

This is NOT a product evaluator or authorization to execute user projects.
It runs a *fixed*, no-mount, no-network guest against the existing pinned
Linux M1 image, then independently audits bounded Boolean observations.
No API, Ollama, ACP, generated project, model or GitHub access is used.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import pathlib
import re
import secrets
import subprocess
import sys
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "m1"))
sys.path.insert(0, str(ROOT / "tools" / "m0"))

from acpx_initialize_probe import OWNER_IMAGE_ID
from acpx_opencode_image_probe import inspect_composed, verify_combined, verify_inputs
from gateway_image_probe import fixed

ARTIFACTS = ROOT / "artifacts" / "m4"
NAME = re.compile(r"ca-m4-node-[0-9a-f]{12}\Z")
CASES = (
    "valid_javascript_parsed",
    "invalid_javascript_rejected",
    "syntax_check_did_not_execute",
    "system_write_denied",
    "symlink_system_write_denied",
    "path_traversal_write_denied",
    "system_credentials_read_denied",
    "elevation_denied",
    "sensitive_environment_absent",
    "external_network_denied",
    "child_external_network_denied",
    "child_system_write_denied",
    "git_remote_escape_denied",
)

# This is constant trusted fixture code, never generated or supplied by a user.
# No untrusted project, worktree, URL, host socket or credential is mounted.
WORKLOAD_JS = r"""
const fs = require('node:fs');
const cp = require('node:child_process');
const net = require('node:net');
const outcomes = {};
function denied(fn) {
  try { fn(); return false; } catch { return true; }
}
function syntaxCheck(program) {
  const result = cp.spawnSync(process.execPath, ['--check', '-'], {
    input: program, encoding: 'utf8', timeout: 3500, maxBuffer: 4096,
    env: { HOME: '/tmp', PATH: process.env.PATH }
  });
  return result.status === 0;
}
function child(program) {
  const result = cp.spawnSync(process.execPath, ['-e', program], {
    encoding: 'utf8', timeout: 4500, maxBuffer: 512,
    env: { HOME: '/tmp', PATH: process.env.PATH }
  });
  return result.status === 0 && result.stdout.trim() === 'DENIED';
}
function cannotConnect(host, port) {
  return new Promise(resolve => {
    const socket = net.createConnection({host,port});
    let finished = false;
    const end = value => {
      if (finished) return;
      finished = true; socket.destroy(); resolve(value);
    };
    socket.setTimeout(1800, () => end(true));
    socket.on('error', () => end(true));
    socket.on('connect', () => end(false));
  });
}
const marker = '/tmp/ca-m4-syntax-never-executed';
const codeWithSideEffect = [
  "const fs = require('node:fs');",
  "fs.writeFileSync('/tmp/ca-m4-syntax-never-executed', 'BAD');"
].join('\n');
outcomes.valid_javascript_parsed = syntaxCheck(codeWithSideEffect);
outcomes.invalid_javascript_rejected = !syntaxCheck('function broken( {');
outcomes.syntax_check_did_not_execute = !fs.existsSync(marker);
outcomes.system_write_denied =
  denied(() => fs.writeFileSync('/etc/ca-m4-write', 'BAD'));
fs.symlinkSync('/etc/passwd','/tmp/ca-m4-escape');
outcomes.symlink_system_write_denied =
  denied(() => fs.writeFileSync('/tmp/ca-m4-escape','BAD'));
outcomes.path_traversal_write_denied =
  denied(() => fs.writeFileSync('/tmp/../etc/ca-m4-traversal','BAD'));
outcomes.system_credentials_read_denied =
  denied(() => fs.readFileSync('/etc/shadow'));
outcomes.elevation_denied =
  process.getuid() !== 0 && denied(() => process.setuid(0));
const secrets = [
  'OPENAI_API_KEY', 'ANTHROPIC_API_KEY', 'GITHUB_TOKEN', 'GH_TOKEN',
  'AWS_ACCESS_KEY_ID', 'AWS_SECRET_ACCESS_KEY',
  'AZURE_OPENAI_API_KEY', 'OLLAMA_HOST', 'OPENCLAW_GATEWAY_TOKEN'
];
outcomes.sensitive_environment_absent =
  secrets.every(key => !Object.hasOwn(process.env, key));
(async () => {
  outcomes.external_network_denied =
    await cannotConnect('203.0.113.1', 443);
  outcomes.child_external_network_denied = child([
    "const net=require('node:net');",
    "const s=net.createConnection({host:'203.0.113.1',port:443});",
    "let done=false;const finish=x=>{if(done)return;done=true;console.log(x);s.destroy();};",
    "s.setTimeout(1500,()=>finish('DENIED'));",
    "s.on('error',()=>finish('DENIED'));",
    "s.on('connect',()=>finish('EXPOSED'));"
  ].join(''));
  outcomes.child_system_write_denied = child([
    "try{require('node:fs').writeFileSync('/etc/ca-m4-child','BAD');",
    "console.log('EXPOSED')}catch{console.log('DENIED')}"
  ].join(''));
  const remote = cp.spawnSync('git', [
    '-c', 'credential.helper=', 'ls-remote',
    'https://example.invalid/ca-m4-git-remote-probe'
  ], {
    encoding:'utf8', timeout:4500, maxBuffer:1024,
    env:{PATH:process.env.PATH,HOME:'/tmp',GIT_TERMINAL_PROMPT:'0'}
  });
  // An unavailable git executable is also an unavailable remote path;
  // the test cannot by itself prove comprehensive network isolation.
  outcomes.git_remote_escape_denied =
    Boolean(remote.error && remote.error.code === 'ENOENT') ||
    (remote.status !== 0 && remote.status !== null);
  process.stdout.write(JSON.stringify(outcomes)+'\n');
})().catch(() => process.exit(2));
"""


def command_for(image_id: str, name: str, uid: int, gid: int) -> list[str]:
    if image_id != OWNER_IMAGE_ID or not NAME.fullmatch(name):
        raise ValueError("Wrong pinned image identity or guest label")
    if uid <= 0 or gid <= 0:
        raise ValueError("Node syntax qualification requires a non-root user")
    return [
        "podman", "run", "--rm", "--pull=never", "--name", name,
        "--network=none", "--read-only", "--cap-drop=all",
        "--security-opt=no-new-privileges", "--userns=keep-id",
        f"--user={uid}:{gid}", "--pids-limit=64", "--memory=1g",
        "--cpus=1",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=128m,notmpcopyup",
        "--env=HOME=/tmp", "--env=XDG_CONFIG_HOME=/tmp/config",
        "--env=XDG_CACHE_HOME=/tmp/cache", "--env=OPENCODE_DISABLE_AUTOUPDATE=1",
        "--workdir=/tmp", "--entrypoint=node",
        image_id, "-e", WORKLOAD_JS,
    ]


def parse_observations(raw: str) -> dict[str, bool]:
    if not 0 < len(raw) < 2800:
        raise ValueError("Missing or oversized qualification output")
    observed = json.loads(raw)
    if type(observed) is not dict or set(observed) != set(CASES):
        raise ValueError("Incomplete or unexpected qualification-case set")
    if any(type(observed[key]) is not bool for key in CASES):
        raise ValueError("Nonboolean guest observation")
    if not all(observed.values()):
        raise ValueError("Syntax/security observation failed: " + ", ".join(
            key for key in CASES if not observed[key]))
    return {key: True for key in CASES}


def run_probe() -> dict:
    result = {
        "schema_version": 1,
        "stage": "M4.1-fixed-fixture-Node-syntax-isolation",
        "recorded_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "BLOCKED",
        "image_id": None,
        "provenance_verified": False,
        "cases": None,
        "passed": 0,
        "required": len(CASES),
        "guest_removed": False,
        "host": sys.platform,
        "model_inference": False,
        "user_source_used": False,
        "source_writes": False,
        "browser_executed": False,
        "runtime_profile_qualified": False,
        "production_execution_enabled": False,
        "reason": None,
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        result["reason"] = "Only a non-root Linux rootless Podman host is supported"
        return result
    name = "ca-m4-node-" + secrets.token_hex(6)
    try:
        source_id = verify_inputs()
        image_id = inspect_composed(source_id)
        if image_id != OWNER_IMAGE_ID:
            raise ValueError("Existing M1 immutable combined image provenance changed")
        result["image_id"] = image_id
        details = verify_combined(image_id)
        if details["registry"] != {
            "id": "opencode", "installation": "installed",
            "expectedCommand": True,
        }:
            raise ValueError("Expected pinned OpenCode image registry unavailable")
        result["provenance_verified"] = True
        guest = fixed(command_for(image_id, name, os.getuid(), os.getgid()),
                      timeout=55)
        if guest.returncode != 0:
            raise ValueError("Pinned syntax guest terminated unexpectedly")
        result["cases"] = parse_observations(guest.stdout)
        result["passed"] = len(result["cases"])
        result["status"] = "NODE_SYNTAX_FIXTURE_PASS"
    except (ValueError, OSError, TypeError, KeyError, json.JSONDecodeError,
            subprocess.TimeoutExpired) as error:
        result["reason"] = str(error)[:300]
    finally:
        try:
            fixed(["podman", "rm", "-f", name], timeout=25)
            exists = fixed(["podman", "container", "exists", name], timeout=15)
            result["guest_removed"] = exists.returncode == 1
        except (OSError, subprocess.TimeoutExpired):
            result["guest_removed"] = False
        if not result["guest_removed"]:
            result["status"] = "BLOCKED"
            result["reason"] = "Disposable guest cleanup not independently confirmed"
    return result


def write_evidence(report: dict) -> pathlib.Path:
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    path = ARTIFACTS / ("node-syntax-qualification-" + uuid.uuid4().hex[:12] + ".json")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as evidence:
        json.dump(report, evidence, sort_keys=True, indent=2)
        evidence.write("\n")
    return path


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", action="store_true",
        help="Explicit opt-in to the pinned, fixed-fixture Linux Podman probe")
    args = parser.parse_args(argv)
    if not args.run:
        print("NOT RUN. Add --run for the fixed-fixture, no-mount qualification.")
        print("This never qualifies task-scoped execution or the Static Web profile.")
        return 0
    report = run_probe()
    path = write_evidence(report)
    print("M4.1", report["status"])
    print("  pinned image:", report["image_id"] or "NOT_VERIFIED")
    print("  fixed guest checks:", report["passed"], "/", report["required"])
    print("  guest cleanup confirmed:", report["guest_removed"])
    print("  model, source execution, browser, owner project: NOT_ATTEMPTED")
    print("  Static Web runtime qualification: NOT_GRANTED")
    if report["reason"]:
        print("  blocker:", report["reason"])
    print("Evidence:", path)
    return 0 if report["status"] == "NODE_SYNTAX_FIXTURE_PASS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
