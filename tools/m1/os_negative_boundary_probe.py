#!/usr/bin/env python3
"""M1.9 adversarial OS checks in disposable no-network rootless Podman.

Run twelve negative operations inside the owner's exact M1.7 immutable
OpenClaw/ACPX/OpenCode image. No model, Gateway, ACP session, project
mount, or remote service is authorized. Observations are a bounded
OS-boundary check, NOT proof that future agent tools are all confined.
"""
from __future__ import annotations

import datetime as dt
import json
import os
import pathlib
import re
import secrets
import subprocess
import sys
import uuid

from acpx_initialize_probe import OWNER_IMAGE_ID
from acpx_opencode_image_probe import inspect_composed, verify_combined, verify_inputs
from gateway_image_probe import fixed

ROOT = pathlib.Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts/m1"
NAME = re.compile(r"ca-m19-negative-[0-9a-f]{12}\Z")
CASES = (
    "system_config_write_denied",
    "symlink_write_escape_denied",
    "path_traversal_write_denied",
    "system_credentials_read_denied",
    "proc_root_credentials_read_denied",
    "host_project_path_unavailable",
    "provider_env_secrets_absent",
    "privilege_elevation_denied",
    "outbound_tcp_denied",
    "host_ollama_loopback_denied",
    "child_process_outbound_denied",
    "child_process_system_write_denied",
)

# The *only* executed adversarial code. Documentation-only IP, no external
# client package; tests operate in the container's /tmp or readonly root.
NEGATIVE_JS = r"""
const fs = require('node:fs');
const net = require('node:net');
const cp = require('node:child_process');
const result = {};
function denied(fn) {
  try { fn(); return false; } catch { return true; }
}
function blockedSocket(host, port) {
  return new Promise(resolve => {
    const socket = net.createConnection({host, port});
    let finished = false;
    const finish = blocked => {
      if (finished) return;
      finished = true;
      socket.destroy();
      resolve(blocked);
    };
    socket.setTimeout(1500, () => finish(true));
    socket.on('error', () => finish(true));
    socket.on('connect', () => finish(false));
  });
}
function child(code) {
  const p = cp.spawnSync(process.execPath, ['-e', code], {
    encoding:'utf8', timeout:4000, maxBuffer:512,
    env:{PATH:process.env.PATH, HOME:'/tmp'}
  });
  return p.status === 0 && p.stdout.trim() === 'DENIED';
}
result.system_config_write_denied =
  denied(() => fs.writeFileSync('/etc/ca-negative-boundary-probe','x',{flag:'wx'}));
const link='/tmp/ca-negative-link';
fs.symlinkSync('/etc/passwd',link);
result.symlink_write_escape_denied =
  denied(() => fs.writeFileSync(link,'x'));
result.path_traversal_write_denied =
  denied(() => fs.writeFileSync('/tmp/../etc/ca-negative-traversal','x'));
result.system_credentials_read_denied =
  denied(() => fs.readFileSync('/etc/shadow'));
result.proc_root_credentials_read_denied =
  denied(() => fs.readFileSync('/proc/1/root/etc/shadow'));
result.host_project_path_unavailable =
  !fs.existsSync('/home/dave/CodingAssistant');
const secrets = [
 'OPENAI_API_KEY','ANTHROPIC_API_KEY','GITHUB_TOKEN','GH_TOKEN',
 'AWS_ACCESS_KEY_ID','AWS_SECRET_ACCESS_KEY','AZURE_OPENAI_API_KEY',
 'AZURE_OPENAI_ENDPOINT','OLLAMA_HOST','OPENCLAW_GATEWAY_TOKEN'
];
result.provider_env_secrets_absent =
  secrets.every(k => !Object.hasOwn(process.env,k));
result.privilege_elevation_denied =
  process.getuid() !== 0 && denied(() => process.setuid(0));
(async()=>{
 result.outbound_tcp_denied = await blockedSocket('203.0.113.1',443);
 result.host_ollama_loopback_denied = await blockedSocket('127.0.0.1',11434);
 result.child_process_outbound_denied = child([
  "const net=require('node:net');",
  "const s=net.createConnection({host:'203.0.113.1',port:443});",
  "let done=false;const finish=(result)=>{if(done)return;done=true;console.log(result);s.destroy();};",
  "s.setTimeout(1500,()=>finish('DENIED'));",
  "s.on('error',()=>finish('DENIED'));",
  "s.on('connect',()=>finish('EXPOSED'));",
 ].join(''));
 result.child_process_system_write_denied = child([
  "try{require('node:fs').writeFileSync('/etc/ca-negative-child','x');",
  "console.log('EXPOSED')}catch{console.log('DENIED')}"
 ].join(''));
 process.stdout.write(JSON.stringify(result)+'\n');
})().catch(()=>process.exit(2));
"""


def command_for(image_id: str, name: str, uid: int, gid: int) -> list[str]:
    if image_id != OWNER_IMAGE_ID or not NAME.fullmatch(name):
        raise ValueError("Owner immutable M1.7 image or disposable name mismatch")
    if uid <= 0 or gid <= 0:
        raise ValueError("Root user prohibited")
    return [
        "podman", "run", "--rm", "--pull=never", "--name", name,
        "--network=none", "--read-only", "--cap-drop=all",
        "--security-opt=no-new-privileges", "--userns=keep-id",
        f"--user={uid}:{gid}", "--pids-limit=96", "--memory=2g",
        "--cpus=2",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=256m,notmpcopyup",
        "--env=HOME=/tmp", "--env=XDG_CONFIG_HOME=/tmp/config",
        "--env=XDG_DATA_HOME=/tmp/data", "--env=XDG_CACHE_HOME=/tmp/cache",
        "--env=OPENCODE_DISABLE_AUTOUPDATE=1",
        "--workdir=/tmp", "--entrypoint=node",
        image_id, "-e", NEGATIVE_JS,
    ]


def parse_negative(raw: str) -> dict[str, bool]:
    if not 0 < len(raw) < 2000:
        raise ValueError("Missing or oversized negative suite output")
    data = json.loads(raw)
    if type(data) is not dict or set(data) != set(CASES):
        raise ValueError("Negative-case set missing or inconsistent")
    if not all(type(data[k]) is bool for k in CASES):
        raise ValueError("Nonboolean isolation observation")
    if not all(data[k] for k in CASES):
        failed = [k for k in CASES if not data[k]]
        raise ValueError("Security boundary not verified: " + ", ".join(failed))
    return {key: True for key in CASES}


def run() -> dict:
    report = {
        "schema_version": 1,
        "stage": "M1.9-isolated-adversarial-negative-controls",
        "recorded_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "BLOCKED", "image_id": None,
        "package_preflight": False, "negative_cases": None,
        "required_cases": len(CASES), "passed_cases": 0,
        "disposable_guest_removed": False,
        "no_network": True, "no_host_mounts": True,
        "no_published_ports": True, "read_only_root": True,
        "rootless": False, "agent_spawned": False,
        "acp_session_created": False, "model_inference": False,
        "host_source_modified": False, "reason": None,
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        report["reason"] = "Linux rootless non-root Podman required"
        return report
    name = "ca-m19-negative-" + secrets.token_hex(6)
    try:
        m0_id = verify_inputs()
        report["rootless"] = True
        image_id = inspect_composed(m0_id)
        if image_id != OWNER_IMAGE_ID:
            raise ValueError("M1.7 combined immutable image provenance changed")
        report["image_id"] = image_id
        installed = verify_combined(image_id)
        if installed["registry"] != {
                "id": "opencode", "installation": "installed",
                "expectedCommand": True}:
            raise ValueError("Pinned OpenCode adapter no longer installed")
        report["package_preflight"] = True
        probe = fixed(command_for(image_id, name, os.getuid(), os.getgid()),
                      timeout=45)
        if probe.returncode != 0:
            raise ValueError("Adversarial guest did not finish (raw logs not retained)")
        results = parse_negative(probe.stdout)
        report["negative_cases"] = results
        report["passed_cases"] = len(results)
        report["status"] = "NEGATIVE_SECURITY_PASS"
    except (ValueError, OSError, TypeError, KeyError, json.JSONDecodeError,
            subprocess.TimeoutExpired) as exc:
        report["reason"] = str(exc)[:450]
    finally:
        try:
            fixed(["podman", "rm", "-f", name], timeout=25)
            exists = fixed(["podman", "container", "exists", name], timeout=15)
            report["disposable_guest_removed"] = exists.returncode == 1
            if not report["disposable_guest_removed"]:
                report["status"] = "BLOCKED"
                report["reason"] = "Disposable security guest cleanup not verified"
        except (OSError, subprocess.TimeoutExpired):
            report["disposable_guest_removed"] = False
            report["status"] = "BLOCKED"
            report["reason"] = "Disposable security guest cleanup failed"
    if not report["disposable_guest_removed"]:
        report["status"] = "BLOCKED"
    return report


def main() -> int:
    result = run()
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    path = ARTIFACTS / ("os-negative-audit-" + uuid.uuid4().hex[:12] + ".json")
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as evidence:
        json.dump(result, evidence, indent=2, sort_keys=True)
        evidence.write("\n")
    print("M1.9", result["status"])
    print("  pinned combined image:", result["image_id"] or "NOT_VERIFIED")
    print("  negative cases passed:", result["passed_cases"], "/", result["required_cases"])
    for key, value in (result["negative_cases"] or {}).items():
        print("  ", key + ":", "DENIED/PASS" if value else "EXPOSED/FAIL")
    print("  disposable guest removed:", result["disposable_guest_removed"])
    print("  Gateway/ACP session/model: NOT_ATTEMPTED")
    if result["reason"]:
        print("  blocker:", result["reason"])
    print("Evidence:", path)
    return 0 if result["status"] == "NEGATIVE_SECURITY_PASS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
