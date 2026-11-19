#!/usr/bin/env python3
"""M1.8: genuine ACPX -> OpenCode ACP initialize, without a coding session.

Use the trusted bundled acpx@0.21.0 runtime's doctor(): it starts exactly
OpenCode 2.3.0 in 'acp' mode, completes ACP initialize, and closes the
child. NO session/new, session/prompt, Ollama, cloud model, host files,
Gateway RPC mutation or source edits. The whole child tree runs inside
one rootless, network=none, read-only, no-host-mount container.
"""
from __future__ import annotations

import datetime as dt
import json
import os
import pathlib
import re
import subprocess
import sys
import uuid
import secrets

from acpx_opencode_image_probe import (
    IMAGE_TAG, GATEWAY_IMAGE_ID, inspect_composed,
    verify_combined, verify_inputs,
)
from gateway_image_probe import fixed

ROOT = pathlib.Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts/m1"
OWNER_IMAGE_ID = "023bf9411fe23045dbdea9c777ed8618dec6553a81417c9aec2d15f7cf5b99ea"
NAME_RE = re.compile(r"ca-m18-init-[0-9a-f]{12}\Z")
PROTOCOL_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,31}\Z")

# This is the ACTUAL pinned ACPX v0.21.0 runtime API, not a synthetic
# JSON-RPC responder. Its doctor() uses the official ACP client and
# performs ONLY initialize; per upstream src/runtime/public/probe.ts
# it deliberately never sends session/new or session/prompt.
ACP_INIT_JS = r"""
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
const requirePlugin = createRequire('/app/dist/extensions/acpx/package.json');
const runtimePath = requirePlugin.resolve('acpx/runtime');
const acpx = await import(pathToFileURL(runtimePath).href);
const expected = '/usr/local/bin/opencode';
let admitted = 0;
let spawned = 0;
const instance = acpx.createAcpRuntime({
  cwd: '/tmp',
  sessionStore: acpx.createRuntimeStore({stateDir: '/tmp/acpx-m18'}),
  agentRegistry: acpx.createAgentRegistry({
    overrides: {opencode: [expected, 'acp']}
  }),
  probeAgent: 'opencode',
  permissionMode: 'deny-all',
  nonInteractivePermissions: 'deny',
  fs: false,
  terminal: false,
  mcpServers: [],
  agentProcessEnvAllow: [],
  agentProcessEnvBlock: [
    'OPENAI_API_KEY','ANTHROPIC_API_KEY','GITHUB_TOKEN','GH_TOKEN',
    'AWS_ACCESS_KEY_ID','AWS_SECRET_ACCESS_KEY','AZURE_OPENAI_API_KEY',
    'OLLAMA_HOST'
  ],
  processLifecycle: {
    onBeforeSpawn(launch) {
      if (launch.scope?.kind !== 'runtime-probe' ||
          launch.scope.agent !== 'opencode' ||
          launch.command !== expected ||
          JSON.stringify(launch.args) !== '["acp"]' ||
          launch.cwd !== '/tmp') {
        throw new Error('Unapproved ACP process launch');
      }
      admitted++;
      if (admitted !== 1) throw new Error('Unexpected extra ACP process');
    },
    onSpawned(proc) {
      if (proc.command !== expected || JSON.stringify(proc.args) !== '["acp"]') {
        throw new Error('Unexpected process was started');
      }
      spawned++;
    }
  },
});
let report;
try {
  report = await instance.doctor();
} catch {
  report = {ok: false, message: 'runtime-doctor-failed', details: []};
}
let cleanup = true;
try { await instance.shutdown(); } catch { cleanup = false; }
const details = Array.isArray(report.details) ? report.details : [];
const versions = details.filter(x => typeof x === 'string' &&
  x.startsWith('protocolVersion=')).map(x => x.slice('protocolVersion='.length));
const protocol = versions.length === 1 ? versions[0] : null;
const ok = report.ok === true &&
  report.message === 'embedded ACP runtime ready' &&
  versions.length === 1 && admitted === 1 && spawned === 1 && cleanup;
process.stdout.write(JSON.stringify({
  ok, protocolVersion: protocol, admitted, spawned, shutdown: cleanup,
  acpInitializeCompleted: report.ok === true,
  sessionCreated: false, promptSent: false,
}) + '\n');
"""


def command_for(image_id: str, name: str, uid: int, gid: int) -> list[str]:
    if image_id != OWNER_IMAGE_ID or not NAME_RE.fullmatch(name):
        raise ValueError("Immutable M1.7 image or disposable name mismatch")
    if uid <= 0 or gid <= 0:
        raise ValueError("Root identity prohibited")
    return [
        "podman", "run", "--rm", "--pull=never", "--name", name,
        "--network=none", "--read-only", "--cap-drop=all",
        "--security-opt=no-new-privileges", "--userns=keep-id",
        f"--user={uid}:{gid}", "--pids-limit=96", "--memory=2g",
        "--cpus=2",
        "--tmpfs=/tmp:rw,nosuid,nodev,size=512m,notmpcopyup",
        "--env=HOME=/tmp", "--env=XDG_CONFIG_HOME=/tmp/config",
        "--env=XDG_DATA_HOME=/tmp/data", "--env=XDG_STATE_HOME=/tmp/state",
        "--env=XDG_CACHE_HOME=/tmp/cache",
        "--env=OPENCODE_DISABLE_AUTOUPDATE=1",
        "--env=OPENCLAW_NO_RESPAWN=1",
        "--workdir=/tmp", "--entrypoint=node",
        image_id, "--input-type=module", "-e", ACP_INIT_JS,
    ]


def parse_result(raw: str) -> dict[str, object]:
    if not 0 < len(raw) <= 1800:
        raise ValueError("Oversized or missing ACP result")
    output = json.loads(raw)
    expected = {
        "ok", "protocolVersion", "admitted", "spawned",
        "shutdown", "acpInitializeCompleted", "sessionCreated", "promptSent",
    }
    if type(output) is not dict or set(output) != expected:
        raise ValueError("Unexpected ACP handshake result schema")
    for field in ("ok", "shutdown", "acpInitializeCompleted",
                  "sessionCreated", "promptSent"):
        if type(output[field]) is not bool:
            raise ValueError("Invalid handshake boolean")
    if type(output["admitted"]) is not int or type(output["spawned"]) is not int:
        raise ValueError("Invalid process counts")
    ver = output["protocolVersion"]
    if type(ver) is not str or not PROTOCOL_RE.fullmatch(ver):
        raise ValueError("Invalid or missing negotiated protocol version")
    if not (output["ok"] and output["shutdown"] and
            output["acpInitializeCompleted"] and
            output["admitted"] == output["spawned"] == 1 and
            output["sessionCreated"] is False and
            output["promptSent"] is False):
        raise ValueError("ACP initialize or deny-by-default lifecycle not verified")
    return {
        "protocol_version": ver,
        "admitted_process_count": 1,
        "spawned_process_count": 1,
        "runtime_shutdown": True,
        "acp_initialize": True,
    }


def probe() -> dict:
    report = {
        "schema_version": 1, "stage": "M1.8-acpx-opencode-initialize",
        "recorded_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "BLOCKED", "image_id": None, "acpx_package_verified": False,
        "protocol": None, "guest_removed": False, "rootless": False,
        "no_network": True, "no_host_mounts": True, "no_ports": True,
        "actual_acp_session_created": False, "model_inference": False,
        "source_modified": False, "reason": None,
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        report["reason"] = "Linux rootless non-root Podman required"
        return report
    name = "ca-m18-init-" + secrets.token_hex(6)
    try:
        m0_id = verify_inputs()
        report["rootless"] = True
        image_id = inspect_composed(m0_id)
        if image_id != OWNER_IMAGE_ID:
            raise ValueError("Composed image diverged from M1.7 owner evidence")
        report["image_id"] = image_id
        installed = verify_combined(image_id)
        if installed["registry"] != {
                "id": "opencode", "installation": "installed",
                "expectedCommand": True}:
            raise ValueError("ACPX installed adapter verification failed")
        report["acpx_package_verified"] = True
        session = fixed(command_for(image_id, name, os.getuid(), os.getgid()),
                        timeout=100)
        if session.returncode != 0:
            raise ValueError("ACP initialization guest failed (no raw logs saved)")
        report["protocol"] = parse_result(session.stdout)
        report["status"] = "ACPX_OPENCODE_INITIALIZE_PASS"
    except (ValueError, TypeError, KeyError, OSError, json.JSONDecodeError,
            subprocess.TimeoutExpired) as error:
        report["reason"] = str(error)[:190]
    finally:
        # Stop/verify only our randomly named container, including timeouts.
        try:
            fixed(["podman", "rm", "-f", name], timeout=25)
            exists = fixed(["podman", "container", "exists", name], timeout=15)
            report["guest_removed"] = exists.returncode == 1
            if not report["guest_removed"]:
                report["status"] = "BLOCKED"
                report["reason"] = "ACP probe guest cleanup not verified"
        except (OSError, subprocess.TimeoutExpired):
            report["guest_removed"] = False
            report["status"] = "BLOCKED"
            report["reason"] = "ACP probe guest cleanup failed"
    if not report["guest_removed"]:
        report["status"] = "BLOCKED"
    return report


def main() -> int:
    result = probe()
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    path = ARTIFACTS / ("acpx-initialize-" + uuid.uuid4().hex[:12] + ".json")
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as file:
        json.dump(result, file, indent=2, sort_keys=True)
        file.write("\n")
    print("M1.8", result["status"])
    print("  pinned composed image:", result["image_id"] or "NOT_VERIFIED")
    print("  installed OpenCode/ACPX:", result["acpx_package_verified"])
    print("  negotiated ACP:", result["protocol"])
    print("  guest cleanup verified:", result["guest_removed"])
    print("  coding session/prompt/model: NOT_ATTEMPTED")
    if result["reason"]:
        print("  blocker:", result["reason"])
    print("Evidence:", path)
    return 0 if result["status"] == "ACPX_OPENCODE_INITIALIZE_PASS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
