// M0.14 model-free fixture isolation proof. This is NEVER an autonomous
// benchmark or a provider. Execute inside no-network read-only-root Podman.
// The only writable mount is a disposable workspace COPY, never host source.
import fs from "node:fs";
import net from "node:net";
import {spawnSync} from "node:child_process";
import path from "node:path";

const [TASK, SENTINEL] = process.argv.slice(1);
const FILES = {
  "01-api-feature": ["api/Api.csproj", "api/Program.cs"],
  "02-build-repair": ["api/Program.cs", "api/Repair.csproj"],
  "03-fullstack-feature": [
    "api/Api.csproj", "api/Program.cs", "web/package.json",
    "web/src/App.tsx", "web/tsconfig.json",
  ],
};
const expected = FILES[TASK];
const checks = {
  non_root: false, no_new_privileges: false, zero_capabilities: false,
  root_read_only: false, external_tcp_denied: false,
  host_sentinel_hidden: false, host_home_hidden: false,
  no_git_control: false, exact_frozen_files: false,
  regular_unlinked_files: false, bounded_fixture_bytes: false,
  disposable_write_works: false, disposable_write_cleaned: false,
  child_non_root: false, child_no_privileges: false,
  child_no_new_privileges: false, child_external_tcp_denied: false,
  tools_present: false,
};

function filesUnder(dir) {
  const answer = [];
  function walk(folder) {
    for (const item of fs.readdirSync(folder, {withFileTypes: true})) {
      const name = path.join(folder, item.name);
      const rel = path.relative(dir, name).replaceAll(path.sep, "/");
      const info = fs.lstatSync(name);
      if (info.isSymbolicLink()) throw new Error("linked file denied");
      if (info.isDirectory()) walk(name);
      else if (info.isFile()) answer.push({
        name: rel, bytes: info.size, regular: info.nlink === 1,
      });
      else throw new Error("unsupported fixture type");
      if (answer.length > 16) throw new Error("fixture file count exceeded");
    }
  }
  walk(dir);
  return answer.sort((a, b) => a.name.localeCompare(b.name));
}
async function externalDenied() {
  return await new Promise(resolve => {
    let done = false;
    const conn = net.connect({host: "1.1.1.1", port: 443});
    function finish(value) {
      if (done) return;
      done = true;
      conn.destroy();
      resolve(value);
    }
    conn.once("connect", () => finish(false));
    conn.once("error", () => finish(true));
    setTimeout(() => finish(false), 1400);
  });
}
function identity() {
  const status = fs.readFileSync("/proc/self/status", "utf8");
  const mount = fs.readFileSync("/proc/self/mountinfo", "utf8")
    .split("\n").find(line => line.split(" - ")[0].split(" ")[4] === "/");
  return {
    non_root: typeof process.getuid === "function" && process.getuid() !== 0,
    no_new_privileges: /^NoNewPrivs:\s*1\s*$/m.test(status),
    zero_capabilities: /^CapEff:\s*0+\s*$/m.test(status),
    root_read_only:
      (mount?.split(" - ")[0].split(" ")[5]?.split(",") || []).includes("ro"),
  };
}
function fixedTool(executable, argumentsList) {
  const result = spawnSync(executable, argumentsList, {
    cwd: "/tmp", timeout: 12000, encoding: "utf8", maxBuffer: 8192,
    env: {
      PATH: "/usr/local/bin:/usr/bin:/bin", HOME: "/tmp",
      DOTNET_CLI_HOME: "/tmp", OPENCODE_DISABLE_AUTOUPDATE: "1",
      XDG_CONFIG_HOME: "/tmp/.config",
    },
  });
  return result.status === 0 && !result.error ? result.stdout.trim() : "";
}

try {
  if (!expected || !SENTINEL || !SENTINEL.startsWith("/"))
    throw new Error("Unknown frozen task or absent sentinel");
  Object.assign(checks, identity());
  checks.external_tcp_denied = await externalDenied();
  checks.host_sentinel_hidden = !fs.existsSync(SENTINEL);
  checks.host_home_hidden = !fs.existsSync("/home/dave") &&
    !fs.existsSync("/home/runner");
  checks.no_git_control = !fs.existsSync("/workspace/.git") &&
    !fs.existsSync("/run/user/1000/podman/podman.sock") &&
    !fs.existsSync("/var/run/docker.sock");
  const snapshot = filesUnder("/workspace");
  checks.exact_frozen_files =
    JSON.stringify(snapshot.map(x => x.name).sort()) ===
      JSON.stringify([...expected].sort());
  checks.regular_unlinked_files = snapshot.every(x => x.regular);
  checks.bounded_fixture_bytes = snapshot.every(x => x.bytes > 0 &&
      x.bytes <= 1024 * 1024) &&
    snapshot.reduce((sum, x) => sum + x.bytes, 0) <= 4 * 1024 * 1024;
  // A single known probe write proves the future agent has a writable COPY,
  // but the marker must be removed. No source file is edited.
  if (checks.exact_frozen_files && checks.regular_unlinked_files) {
    const probe = "/workspace/.m014-disposable-write-check";
    const fd = fs.openSync(probe, "wx", 0o600);
    try {
      fs.writeSync(fd, "disposable only");
      checks.disposable_write_works =
        fs.readFileSync(probe, "utf8") === "disposable only";
    } finally {
      fs.closeSync(fd);
      fs.unlinkSync(probe);
    }
    checks.disposable_write_cleaned =
      JSON.stringify(filesUnder("/workspace")) === JSON.stringify(snapshot);
  }
  // Check inherited child restrictions; no shell or generated command.
  const child = spawnSync("node", ["-e", `
    const fs = require("fs");
    const s = fs.readFileSync("/proc/self/status", "utf8");
    console.log(JSON.stringify({
      non_root: process.getuid() !== 0,
      no_privileges: /^CapEff:\\s*0+\\s*$/m.test(s),
      no_new_privileges: /^NoNewPrivs:\\s*1\\s*$/m.test(s),
    }));
  `], {
    cwd: "/tmp", timeout: 5000, encoding: "utf8", maxBuffer: 4096,
    env: {PATH: "/usr/local/bin:/usr/bin:/bin", HOME: "/tmp"},
  });
  if (child.status === 0 && !child.error) {
    const result = JSON.parse(child.stdout);
    checks.child_non_root = result.non_root === true;
    checks.child_no_privileges = result.no_privileges === true;
    checks.child_no_new_privileges = result.no_new_privileges === true;
  }
  // Both parent and any child inherit the container's network namespace.
  // Test child network separately with a fixed literal one-shot check.
  const childNet = spawnSync("node", ["-e", `
    const net = require("net");
    const s = net.connect({host: "1.1.1.1", port: 443});
    s.on("connect", () => { console.log("CONNECTED"); s.destroy(); });
    s.on("error", () => { console.log("DENIED"); });
    setTimeout(() => { console.log("TIMEOUT"); s.destroy(); }, 1200);
  `], {cwd: "/tmp", timeout: 3500, encoding: "utf8", maxBuffer: 4096,
        env: {PATH: "/usr/local/bin:/usr/bin:/bin", HOME: "/tmp"}});
  checks.child_external_tcp_denied =
    childNet.status === 0 && !childNet.error &&
    childNet.stdout.includes("DENIED") &&
    !childNet.stdout.includes("CONNECTED");
  checks.tools_present =
    /^10\./.test(fixedTool("dotnet", ["--version"])) &&
    /^v22\./.test(fixedTool("node", ["--version"])) &&
    /^\d+\./.test(fixedTool("npm", ["--version"])) &&
    /2\.3\.0/.test(fixedTool("opencode", ["--version"]));
} catch (error) {
  // Never emit project contents, host files, or secrets on failure.
  console.error("M0.14 fixture guard blocked:", String(error?.name || "error"));
}
console.log(JSON.stringify({task: TASK, checks}));
if (!Object.values(checks).every(x => x === true)) process.exitCode = 2;
