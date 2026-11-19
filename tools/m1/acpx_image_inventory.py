#!/usr/bin/env python3
"""M1.3 static ACPX packaging inventory of already-qualified Gateway image.

Inspect only fixed metadata paths INSIDE the pinned, rootless, offline OpenClaw
container. Do not start Gateway/ACP, invoke any adapter, install plugins,
run network operations, mount host files, load provider configuration or call
Ollama. This is not proof that ACP works.
"""
from __future__ import annotations

import datetime as dt
import json
import os
import pathlib
import subprocess
import sys

from gateway_image_probe import fixed, inspected_image
from gateway_loopback_probe import APPROVED_IMAGE_ID, APPROVED_REPO_DIGEST

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "artifacts/m1/acpx-image-inventory.json"
SOURCE = r"""
"use strict";
const fs = require("node:fs");
const paths = Object.freeze({
  manifest: "/app/dist/extensions/acpx/openclaw.plugin.json",
  entry: "/app/dist/extensions/acpx/index.js",
  package: "/app/dist/extensions/acpx/package.json",
  dependencyAtRoot: "/app/node_modules/acpx/package.json",
  dependencyAtPlugin: "/app/dist/extensions/acpx/node_modules/acpx/package.json",
});
function regularFile(p) {
  try {
    const st = fs.lstatSync(p);
    return st.isFile() && !st.isSymbolicLink() && st.size > 0 && st.size <= 262144;
  } catch { return false; }
}
function versionFile(p, kind) {
  if (!regularFile(p)) return null;
  try {
    const json = JSON.parse(fs.readFileSync(p, "utf8"));
    if (kind === "manifest") {
      return json.id === "acpx" ? "acpx" : "UNEXPECTED_ID";
    }
    if (kind === "pluginPackage") {
      return json.name === "@openclaw/acpx"
        ? String(json.version ?? "").slice(0, 32) : "UNEXPECTED_NAME";
    }
    return json.name === "acpx"
      ? String(json.version ?? "").slice(0, 32) : "UNEXPECTED_NAME";
  } catch { return "MALFORMED"; }
}
const present = {};
for (const [name, file] of Object.entries(paths))
  present[name] = regularFile(file);
const result = {
  manifest: versionFile(paths.manifest, "manifest"),
  pluginPackageVersion: versionFile(paths.package, "pluginPackage"),
  acpxDependencyRoot: versionFile(paths.dependencyAtRoot, "dependency"),
  acpxDependencyPlugin: versionFile(paths.dependencyAtPlugin, "dependency"),
  present,
};
process.stdout.write(JSON.stringify(result) + "\n");
"""


def command(image_id: str, uid: int, gid: int) -> list[str]:
    if image_id != APPROVED_IMAGE_ID or uid <= 0 or gid <= 0:
        raise ValueError("Unapproved image or identity")
    return [
        "podman", "run", "--rm", "--pull=never", "--network=none",
        "--read-only", "--cap-drop=all", "--security-opt=no-new-privileges",
        "--userns=keep-id", f"--user={uid}:{gid}", "--pids-limit=64",
        "--memory=2g", "--tmpfs=/tmp:rw,nosuid,nodev,size=128m,notmpcopyup",
        "--env=HOME=/tmp", "--env=XDG_CONFIG_HOME=/tmp/config",
        "--workdir=/app", "--entrypoint=node",
        image_id, "-e", SOURCE,
    ]


def parse_report(raw: str) -> dict:
    if len(raw) > 3500:
        raise ValueError("Unexpectedly large guest inventory")
    data = json.loads(raw)
    expected = {
        "manifest", "pluginPackageVersion", "acpxDependencyRoot",
        "acpxDependencyPlugin", "present",
    }
    flags = {
        "manifest", "entry", "package", "dependencyAtRoot",
        "dependencyAtPlugin",
    }
    if type(data) is not dict or set(data) != expected:
        raise ValueError("Unexpected static inventory schema")
    if type(data["present"]) is not dict or set(data["present"]) != flags or any(
            type(x) is not bool for x in data["present"].values()):
        raise ValueError("Invalid static path availability")
    for field in expected - {"present"}:
        x = data[field]
        if x is not None and (
                type(x) is not str or not x or len(x) > 32 or not all(
                    c.isascii() and (c.isalnum() or c in ".-+_") for c in x)):
            raise ValueError("Invalid plugin metadata")
    if data["present"]["manifest"] != (data["manifest"] is not None):
        raise ValueError("Manifest metadata contradicts file availability")
    for name, field in (
        ("package", "pluginPackageVersion"),
        ("dependencyAtRoot", "acpxDependencyRoot"),
        ("dependencyAtPlugin", "acpxDependencyPlugin"),
    ):
        if data["present"][name] != (data[field] is not None):
            raise ValueError("Plugin metadata contradicts file availability")
    return data


def run() -> dict:
    result = {
        "schema_version": 1, "stage": "M1.3-acpx-static-image-inventory",
        "recorded_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "BLOCKED", "image_id": None, "repo_digest": None,
        "plugin_files": None, "plugin_distribution": "NOT_INSPECTED",
        "gateway_started": False, "acp_session_started": False,
        "model_inference": False, "host_node_updated": False,
        "plugins_installed": False, "image_pulled": False,
        "host_files_mounted": False, "reason": None,
    }
    if sys.platform != "linux" or os.geteuid() == 0:
        result["reason"] = "Linux rootless Podman non-root required"
        return result
    try:
        rootless = fixed(["podman", "info", "--format",
                          "{{.Host.Security.Rootless}}"], timeout=25)
        if rootless.returncode != 0 or rootless.stdout.strip() != "true":
            raise ValueError("Podman rootless mode not verified")
        known = inspected_image()
        if known is None or known["image_id"] != APPROVED_IMAGE_ID or (
                known["repo_digest"] != APPROVED_REPO_DIGEST):
            raise ValueError("Official M1.1 immutable image identity changed")
        result.update(known)
        guest = fixed(command(known["image_id"], os.getuid(), os.getgid()),
                      timeout=90)
        if guest.returncode != 0:
            raise ValueError("Static image inventory guest failed")
        data = parse_report(guest.stdout)
        result["plugin_files"] = data
        present = data["present"]
        if not any(present.values()):
            result["plugin_distribution"] = "NOT_BUNDLED"
        elif (data["manifest"] == "acpx" and present["entry"] and
              data["pluginPackageVersion"] == "2026.11.5" and (
                  data["acpxDependencyRoot"] == "0.21.0" or
                  data["acpxDependencyPlugin"] == "0.21.0")):
            result["plugin_distribution"] = "FILES_PRESENT_NOT_ACTIVATED"
        else:
            result["plugin_distribution"] = "INCOMPLETE_OR_UNEXPECTED"
        # A missing optional plugin is a successful discovery, but never a
        # PASS for ACP readiness. No code in this probe can enable ACP.
        result["status"] = "INVENTORY_COMPLETE"
    except (ValueError, KeyError, TypeError, OSError,
            subprocess.TimeoutExpired, json.JSONDecodeError) as exc:
        result["reason"] = str(exc)[:180]
    return result


def main() -> int:
    report = run()
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(json.dumps(report, sort_keys=True, indent=2) + "\n",
                      encoding="utf-8")
    print("M1.3", report["status"])
    print("  image:", report["image_id"] or "NOT_VERIFIED")
    print("  ACPX distribution:", report["plugin_distribution"])
    if report["plugin_files"]:
        print("  fixed-path files:", report["plugin_files"]["present"])
        print("  package version:", report["plugin_files"]["pluginPackageVersion"])
        print("  embedded acpx dependency:",
              report["plugin_files"]["acpxDependencyPlugin"] or
              report["plugin_files"]["acpxDependencyRoot"])
    print("  Gateway/ACP/model/plugin installation: NOT_ATTEMPTED")
    if report["reason"]:
        print("  blocker:", report["reason"])
    print("Evidence:", OUTPUT)
    return 0 if report["status"] == "INVENTORY_COMPLETE" else 2


if __name__ == "__main__":
    sys.exit(main())
