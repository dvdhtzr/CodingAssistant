// M0.9 deterministic workspace and child-process isolation preflight.
// Only a new, disposable fixture copy is mounted. No agent/model is invoked.
import fs from "node:fs";
import net from "node:net";
import path from "node:path";
import {spawnSync} from "node:child_process";

const sentinel = process.argv[1];
const WORK = "/workspace";
const checks = {
  non_root: false, root_read_only: false, no_effective_capabilities: false,
  no_new_privileges: false, fixture_readable: false,
  temporary_workspace_write: false, host_sentinel_hidden: false,
  no_external_tcp: false, child_non_root: false, child_root_read_only: false,
  child_no_effective_capabilities: false, child_no_new_privileges: false,
  child_host_sentinel_hidden: false, child_no_external_tcp: false,
};
async function tcpDenied() {
  return await new Promise(resolve => {
    let completed = false;
    const socket = net.connect({host: "1.1.1.1", port: 443});
    const complete = value => {
      if (completed) return;
      completed = true;
      socket.destroy();
      resolve(value);
    };
    socket.once("connect", () => complete(false));
    socket.once("error", () => complete(true));
    setTimeout(() => complete(false), 1800);
  });
}
try {
  const proc = fs.readFileSync("/proc/self/status", "utf8");
  const root = fs.readFileSync("/proc/self/mountinfo", "utf8").split("\n")
    .find(line => line.split(" - ")[0].split(" ")[4] === "/");
  checks.non_root = typeof process.getuid === "function" && process.getuid() !== 0;
  checks.root_read_only = (root?.split(" - ")[0].split(" ")[5]?.split(",") || []).includes("ro");
  checks.no_effective_capabilities = /^CapEff:\s*0+\s*$/m.test(proc);
  checks.no_new_privileges = /^NoNewPrivs:\s*1\s*$/m.test(proc);
  checks.host_sentinel_hidden = !fs.existsSync(sentinel);
  checks.fixture_readable = ["Program.cs", "Repair.csproj"].every(name => {
    const file = path.join(WORK, name);
    return fs.statSync(file).isFile() && fs.readFileSync(file).length > 0;
  });
  const marker = path.join(WORK, ".m0-9-fixed-write-check");
  try {
    fs.writeFileSync(marker, "disposable only", {flag: "wx", mode: 0o600});
    checks.temporary_workspace_write = fs.readFileSync(marker, "utf8") === "disposable only";
  } finally {
    try { fs.unlinkSync(marker); } catch (_) { checks.temporary_workspace_write = false; }
  }
  checks.no_external_tcp = await tcpDenied();
  // Identical fixed kernel-negative checks in a child process. No generated code.
  const childSource = [
    'const fs=require("node:fs"),net=require("node:net");',
    'const secret=process.argv[1];',
    'const proc=fs.readFileSync("/proc/self/status","utf8");',
    'const root=fs.readFileSync("/proc/self/mountinfo","utf8").split("\\n").find(x=>x.split(" - ")[0].split(" ")[4]==="/");',
    'const checks={non_root:process.getuid()!==0,root_read_only:(root?.split(" - ")[0].split(" ")[5]?.split(",")||[]).includes("ro"),no_effective_capabilities:/^CapEff:\\s*0+\\s*$/m.test(proc),no_new_privileges:/^NoNewPrivs:\\s*1\\s*$/m.test(proc),host_sentinel_hidden:!fs.existsSync(secret),no_external_tcp:false};',
    'let done=false;const s=net.connect({host:"1.1.1.1",port:443});',
    'function finish(denied){if(done)return;done=true;s.destroy();checks.no_external_tcp=denied;console.log(JSON.stringify(checks));}',
    's.once("error",()=>finish(true));s.once("connect",()=>finish(false));setTimeout(()=>finish(false),1800);',
  ].join("\n");
  const child = spawnSync(process.execPath, ["-e", childSource, sentinel], {
    cwd: WORK, timeout: 5500, encoding: "utf8", maxBuffer: 65536,
    env: {PATH: "/usr/local/bin:/usr/bin:/bin", HOME: "/tmp"},
  });
  if (child.status === 0 && !child.error) {
    const inner = JSON.parse(child.stdout);
    for (const key of ["non_root", "root_read_only", "no_effective_capabilities",
                       "no_new_privileges", "host_sentinel_hidden", "no_external_tcp"]) {
      checks["child_" + key] = inner[key] === true;
    }
  }
} catch (_) {
  // Fail closed on all missing evidence.
}
console.log(JSON.stringify({checks}));
if (!Object.values(checks).every(value => value === true)) process.exitCode = 2;
