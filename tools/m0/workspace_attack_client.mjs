// M0.10 deterministic attack probes against a temporary workspace copy.
// No model, no OpenCode, no generated script/commands. No host repository mounts.
import fs from "node:fs";
import net from "node:net";
import path from "node:path";
import {spawnSync} from "node:child_process";

const WORK = "/workspace";
const outside = process.argv[1];
const checks = {
  non_root_identity: false,
  no_effective_capabilities: false,
  no_new_privileges: false,
  root_mount_read_only: false,
  host_sentinel_not_visible: false,
  root_config_write_denied: false,
  relative_traversal_write_denied: false,
  symlink_outside_write_denied: false,
  symlink_host_read_denied: false,
  disposable_workspace_write_available: false,
  unrelated_git_repository_absent: false,
  private_broker_socket_absent: false,
  child_identity_and_privileges_held: false,
  child_external_tcp_denied: false,
  child_host_sentinel_not_visible: false,
  git_remote_connection_denied: false,
};

function rootMountIsReadOnly() {
  const line = fs.readFileSync("/proc/self/mountinfo", "utf8").split("\n")
    .find(entry => entry.split(" - ")[0].split(" ")[4] === "/");
  return (line?.split(" - ")[0].split(" ")[5]?.split(",") || []).includes("ro");
}
function fixedWriteDenied(filename) {
  let succeeded = false;
  try {
    fs.writeFileSync(filename, "M0.10 forbidden", {flag: "wx", mode: 0o600});
    succeeded = true;
  } catch (_) { /* expected EROFS, EACCES, ENOENT or EPERM */ }
  if (succeeded) {
    try { fs.unlinkSync(filename); } catch (_) { /* report fail */ }
  }
  return !succeeded;
}
function escapeLink(linkName, target, operation) {
  const link = path.join(WORK, linkName);
  let denied = false;
  try {
    fs.symlinkSync(target, link);
    if (operation === "read") {
      try { fs.readFileSync(link); }
      catch (_) { denied = true; }
    } else {
      // "wx" would reject the symlink because its directory entry exists,
      // producing a false positive. Attempt a real follow-symlink write.
      try {
        fs.writeFileSync(link, "M0.10 forbidden symlink write", {flag: "w"});
      } catch (_) { denied = true; }
      if (!denied) {
        try { fs.unlinkSync(target); } catch (_) { /* report FAIL */ }
      }
    }
  } finally {
    try { fs.unlinkSync(link); } catch (_) { denied = false; }
  }
  return denied;
}
try {
  const proc = fs.readFileSync("/proc/self/status", "utf8");
  checks.non_root_identity = typeof process.getuid === "function" && process.getuid() !== 0;
  checks.no_effective_capabilities = /^CapEff:\s*0+\s*$/m.test(proc);
  checks.no_new_privileges = /^NoNewPrivs:\s*1\s*$/m.test(proc);
  checks.root_mount_read_only = rootMountIsReadOnly();
  checks.host_sentinel_not_visible = !fs.existsSync(outside);
  checks.root_config_write_denied = fixedWriteDenied("/etc/codingassistant-m010-denied");
  checks.relative_traversal_write_denied =
    fixedWriteDenied(WORK + "/../etc/codingassistant-m010-traversal");
  checks.symlink_outside_write_denied = escapeLink(
    ".m010-link-outside", "/etc/codingassistant-m010-symlink", "write");
  checks.symlink_host_read_denied = escapeLink(
    ".m010-link-host", outside, "read");
  checks.unrelated_git_repository_absent =
    !fs.existsSync(path.join(WORK, ".git")) &&
    !fs.existsSync(path.join(WORK, ".gitconfig")) &&
    !fs.existsSync(path.join(WORK, ".env"));
  checks.private_broker_socket_absent =
    !fs.existsSync("/run/codingassistant/ollama.sock");
  const temp = path.join(WORK, ".m010-temporary-write");
  try {
    fs.writeFileSync(temp, "M0.10 disposable workspace", {flag: "wx", mode: 0o600});
    checks.disposable_workspace_write_available =
      fs.readFileSync(temp, "utf8") === "M0.10 disposable workspace";
  } finally {
    try { fs.unlinkSync(temp); }
    catch (_) { checks.disposable_workspace_write_available = false; }
  }

  // Static child code is deliberately identical across runs; no model-produced text.
  const child = [
    'const fs=require("node:fs"),net=require("node:net");',
    'const hidden=process.argv[1];',
    'const text=fs.readFileSync("/proc/self/status","utf8");',
    'const readOnly=(fs.readFileSync("/proc/self/mountinfo","utf8").split("\\n").find(x=>x.split(" - ")[0].split(" ")[4]==="/")?.split(" - ")[0].split(" ")[5]?.split(",")||[]).includes("ro");',
    'const output={secure:process.getuid()!==0&&/^CapEff:\\s*0+\\s*$/m.test(text)&&/^NoNewPrivs:\\s*1\\s*$/m.test(text)&&readOnly,hidden:!fs.existsSync(hidden),networkDenied:false};',
    'let done=false;const s=net.connect({host:"1.1.1.1",port:443});',
    'function finish(denied){if(done)return;done=true;s.destroy();output.networkDenied=denied;console.log(JSON.stringify(output));}',
    's.once("error",()=>finish(true));s.once("connect",()=>finish(false));setTimeout(()=>finish(false),2000);',
  ].join("\n");
  const nested = spawnSync(process.execPath, ["-e", child, outside], {
    cwd: WORK, timeout: 6500, maxBuffer: 16384, encoding: "utf8",
    env: {PATH: "/usr/local/bin:/usr/bin:/bin", HOME: "/tmp"},
  });
  if (nested.status === 0 && !nested.error) {
    const outcome = JSON.parse(nested.stdout);
    checks.child_identity_and_privileges_held = outcome.secure === true;
    checks.child_external_tcp_denied = outcome.networkDenied === true;
    checks.child_host_sentinel_not_visible = outcome.hidden === true;
  }

  const version = spawnSync("git", ["--version"], {
    cwd: WORK, timeout: 3000, encoding: "utf8", maxBuffer: 8192,
    env: {PATH: "/usr/local/bin:/usr/bin:/bin", HOME: "/tmp"},
  });
  if (version.status === 0) {
    const external = spawnSync("git", [
      "ls-remote", "https://example.com/nonexistent-git-repository", "HEAD"
    ], {
      cwd: WORK, timeout: 7000, encoding: "utf8", maxBuffer: 16384,
      env: {
        PATH: "/usr/local/bin:/usr/bin:/bin", HOME: "/tmp",
        GIT_CONFIG_NOSYSTEM: "1", GIT_CONFIG_GLOBAL: "/dev/null",
        GIT_TERMINAL_PROMPT: "0", GIT_ALLOW_PROTOCOL: "https",
        GIT_ASKPASS: "/bin/false",
      },
    });
    checks.git_remote_connection_denied =
      external.status !== 0 && (external.status !== null ||
        external.error?.code === "ETIMEDOUT");
  }
} catch (_) {
  // Any missing evidence fails closed. Never print raw files or command output.
}
console.log(JSON.stringify({checks}));
if (!Object.values(checks).every(value => value === true)) process.exitCode = 2;
