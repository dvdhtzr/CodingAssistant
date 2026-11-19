// M0 independent acceptance evaluator. Fixed trusted code; never forwarded from
// model. Runs generated app ONLY inside non-root, no-network, read-only-root
// Podman/Docker with one disposable workspace mount and NO inference socket.
import fs from "node:fs";
import path from "node:path";
import net from "node:net";
import {spawn, spawnSync} from "node:child_process";

const [TASK, OUTSIDE] = process.argv.slice(1);
const TASKS = new Set(["01-api-feature", "02-build-repair", "03-fullstack-feature"]);
const guards = {
  non_root: false, no_new_privileges: false, zero_capabilities: false,
  root_read_only: false, external_tcp_denied: false,
  host_sentinel_hidden: false, host_git_hidden: false,
  inference_socket_hidden: false, original_sources_regular: false,
  trusted_tests_available: false, network_not_required: true,
};
const checks = {
  offline_restore: false, offline_build: false, health: false,
  feature_behavior: false, frontend_typecheck: TASK !== "03-fullstack-feature",
  frontend_behavior: TASK !== "03-fullstack-feature",
  injected_test_cleaned: TASK !== "03-fullstack-feature",
  offline_node_dependencies: TASK !== "03-fullstack-feature",
};
const notes = [];
const ROOT = "/workspace";
const TRUSTED_REACT_TEST = "/opt/qualification/TaskSummary.acceptance.test.tsx";
const TRUSTED_NODE_MODULES = "/opt/qualification/web/node_modules";
const SOURCE_NAMES = {
  "01-api-feature": ["api/Api.csproj", "api/Program.cs"],
  "02-build-repair": ["api/Repair.csproj", "api/Program.cs"],
  "03-fullstack-feature": [
    "api/Api.csproj", "api/Program.cs",
    "web/package.json", "web/tsconfig.json", "web/src/App.tsx",
    "web/src/TaskSummary.tsx",
  ],
};

function note(tag) {
  if (notes.length < 12) notes.push(tag);
}
function fixed(command, args, cwd, timeout = 90000) {
  const env = {
    PATH: "/usr/local/bin:/usr/bin:/bin", HOME: "/tmp",
    DOTNET_CLI_HOME: "/tmp", DOTNET_NOLOGO: "1",
    DOTNET_CLI_TELEMETRY_OPTOUT: "1",
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE: "1",
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE: "1",
    NUGET_PACKAGES: "/tmp/.nuget/packages",
    NPM_CONFIG_OFFLINE: "true", NPM_CONFIG_AUDIT: "false",
    OPENCODE_DISABLE_AUTOUPDATE: "1", CI: "1",
    NODE_ENV: "test", NO_COLOR: "1",
    TMPDIR: "/tmp",
  };
  const out = spawnSync(command, args, {
    cwd, env, timeout, stdio: ["ignore", "pipe", "pipe"],
    encoding: "utf8", maxBuffer: 131072,
  });
  return {ok: out.status === 0 && !out.error,
          status: out.status, timeout: out.error?.code === "ETIMEDOUT"};
}
function regular(rel) {
  const candidate = path.join(ROOT, rel);
  const stat = fs.lstatSync(candidate);
  return stat.isFile() && !stat.isSymbolicLink() && stat.nlink === 1 &&
    stat.size > 0 && stat.size < 131072;
}
async function externalDenied() {
  return new Promise(resolve => {
    let done = false;
    const conn = net.connect({host: "1.1.1.1", port: 443});
    const finish = value => {
      if (done) return;
      done = true;
      conn.destroy();
      resolve(value);
    };
    conn.once("connect", () => finish(false));
    conn.once("error", () => finish(true));
    setTimeout(() => finish(false), 1500);
  });
}
function checkGuardFiles() {
  const status = fs.readFileSync("/proc/self/status", "utf8");
  const rootMount = fs.readFileSync("/proc/self/mountinfo", "utf8")
    .split("\n").find(line => line.split(" - ")[0].split(" ")[4] === "/");
  guards.non_root = typeof process.getuid === "function" && process.getuid() !== 0;
  guards.no_new_privileges = /^NoNewPrivs:\s*1\s*$/m.test(status);
  guards.zero_capabilities = /^CapEff:\s*0+\s*$/m.test(status);
  guards.root_read_only =
    (rootMount?.split(" - ")[0].split(" ")[5]?.split(",") || []).includes("ro");
  guards.host_sentinel_hidden = !fs.existsSync(OUTSIDE || "");
  guards.host_git_hidden = !fs.existsSync("/workspace/.git") &&
    !fs.existsSync("/var/run/docker.sock") &&
    !fs.existsSync("/run/user/1000/podman/podman.sock");
  guards.inference_socket_hidden = !fs.existsSync("/run/codingassistant/ollama.sock");
  guards.original_sources_regular = TASKS.has(TASK) &&
    SOURCE_NAMES[TASK].every(regular);
  guards.trusted_tests_available = fs.existsSync(TRUSTED_REACT_TEST) &&
    fs.existsSync(TRUSTED_NODE_MODULES);
}
function isObject(value) { return value && typeof value === "object"; }
function same(value, expected) {
  return isObject(value) &&
    JSON.stringify(value) === JSON.stringify(expected);
}
async function httpChecks(task) {
  const cwd = path.join(ROOT, "api");
  const dll = path.join(cwd, "bin/Release/net10.0/Api.dll");
  const server = net.createServer();
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const port = server.address().port;
  await new Promise(resolve => server.close(resolve));
  const child = spawn("dotnet", [dll], {
    cwd, env: {
      PATH: "/usr/local/bin:/usr/bin:/bin", HOME: "/tmp",
      DOTNET_CLI_HOME: "/tmp", DOTNET_NOLOGO: "1",
      DOTNET_CLI_TELEMETRY_OPTOUT: "1",
      ASPNETCORE_URLS: "http://127.0.0.1:" + port,
    },
    stdio: ["ignore", "ignore", "ignore"],
  });
  const request = async endpoint => {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 2400);
    try {
      const response = await fetch("http://127.0.0.1:" + port + endpoint,
        {signal: controller.signal, redirect: "manual"});
      let json = null;
      try { json = await response.json(); } catch (_) {}
      return {status: response.status, json};
    } catch (_) {
      return {status: 0, json: null};
    } finally { clearTimeout(timer); }
  };
  try {
    let health = {status: 0};
    for (let i = 0; i < 50; i++) {
      health = await request("/health");
      if (health.status === 200 || child.exitCode !== null) break;
      await new Promise(resolve => setTimeout(resolve, 120));
    }
    checks.health = health.status === 200 && same(health.json, {status: "ok"});
    if (!checks.health) return;
    if (task === "01-api-feature") {
      const a = await request("/api/math/add?a=2&b=3");
      const b = await request("/api/math/add?a=-4&b=3");
      const c = await request("/api/math/add?a=1");
      const d = await request("/api/math/add?a=xyz&b=3");
      checks.feature_behavior = a.status === 200 && same(a.json, {sum: 5}) &&
        b.status === 200 && same(b.json, {sum: -1}) &&
        c.status === 400 && d.status === 400;
    } else {
      const a = await request("/api/tasks/summary");
      checks.feature_behavior = a.status === 200 &&
        same(a.json, {total: 3, completed: 2});
    }
  } finally {
    child.kill("SIGKILL");
    await Promise.race([
      new Promise(resolve => child.once("close", resolve)),
      new Promise(resolve => setTimeout(resolve, 2500)),
    ]);
  }
}
function reactChecks() {
  const web = path.join(ROOT, "web");
  const trustedTest = path.join(web, "src/TaskSummary.acceptance.test.tsx");
  const modules = path.join(web, "node_modules");
  if (fs.existsSync(trustedTest) || fs.existsSync(modules)) {
    note("untrusted_acceptance_test_or_dependencies");
    return;
  }
  let injected = false, linked = false;
  try {
    fs.symlinkSync(TRUSTED_NODE_MODULES, modules, "dir");
    linked = true;
    fs.copyFileSync(TRUSTED_REACT_TEST, trustedTest, fs.constants.COPYFILE_EXCL);
    injected = true;
    checks.offline_node_dependencies =
      fs.realpathSync(modules) === TRUSTED_NODE_MODULES &&
      fs.existsSync(path.join(modules, ".bin/tsc")) &&
      fs.existsSync(path.join(modules, ".bin/vitest"));
    if (!checks.offline_node_dependencies) return;
    checks.frontend_typecheck = fixed(path.join(modules, ".bin/tsc"),
      ["--noEmit"], web, 60000).ok;
    if (!checks.frontend_typecheck) return;
    checks.frontend_behavior = fixed(path.join(modules, ".bin/vitest"), [
      "run", "--environment", "jsdom", "--maxWorkers", "1",
      "--no-file-parallelism", "src/TaskSummary.acceptance.test.tsx",
    ], web, 90000).ok;
  } finally {
    if (injected) fs.unlinkSync(trustedTest);
    if (linked) fs.unlinkSync(modules);
    checks.injected_test_cleaned =
      !fs.existsSync(trustedTest) && !fs.existsSync(modules);
  }
}

try {
  checkGuardFiles();
  guards.external_tcp_denied = await externalDenied();
  if (Object.values(guards).every(x => x === true)) {
    const project = TASK === "02-build-repair" ? "Repair.csproj" : "Api.csproj";
    fs.mkdirSync("/tmp/m0-empty-nuget", {recursive: true});
    checks.offline_restore = fixed("dotnet", [
      "restore", project, "--source", "/tmp/m0-empty-nuget",
      "-p:NuGetAudit=false", "--disable-parallel", "-v:q",
    ], "/workspace/api").ok;
    if (checks.offline_restore) {
      checks.offline_build = fixed("dotnet", [
        "build", project, "--no-restore", "-c", "Release",
        "-p:UseSharedCompilation=false", "-v:q",
      ], "/workspace/api").ok;
      if (checks.offline_build) {
        if (TASK === "02-build-repair") {
          const result = spawnSync("dotnet", [
            "/workspace/api/bin/Release/net10.0/Repair.dll",
          ], {cwd: "/workspace/api", timeout: 12000,
              env: {PATH: "/usr/local/bin:/usr/bin:/bin", HOME: "/tmp"},
              encoding: "utf8", stdio: ["ignore", "pipe", "pipe"]});
          checks.feature_behavior = result.status === 0 &&
            result.stdout.replaceAll("\r\n", "\n") === "Total: 15\n";
          checks.health = true; // console fixture has no HTTP health
        } else {
          await httpChecks(TASK);
          if (TASK === "03-fullstack-feature" && checks.feature_behavior &&
              checks.health) reactChecks();
        }
      }
    }
  }
} catch (error) {
  // No generated source, model response or build logs emitted.
  note("evaluator_" + String(error?.name || "error").slice(0, 40));
}
const safe = Object.values(guards).every(x => x === true);
const verified = safe && Object.values(checks).every(x => x === true);
console.log(JSON.stringify({task: TASK, guards, checks, verified, notes}));
if (!verified) process.exitCode = 2;
