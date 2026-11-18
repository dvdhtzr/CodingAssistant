// M0.11 fixed independent C# evaluator. Runs only inside no-network,
 // rootless, read-only-root Podman, with one disposable fixture copy mounted.
 // Not an autonomous agent or an arbitrary command executor.
import fs from "node:fs";
import net from "node:net";
import {spawnSync} from "node:child_process";

const required = {
  non_root: false,
  no_new_privileges: false,
  no_effective_capabilities: false,
  root_read_only: false,
  no_external_tcp: false,
  source_is_regular: false,
  project_is_regular: false,
  offline_restore_passed: false,
  offline_build_passed: false,
  output_exact: false,
};

function runFixed(command, args, maxMs) {
  const env = {
    PATH: "/usr/local/bin:/usr/bin:/bin", HOME: "/tmp",
    DOTNET_NOLOGO: "1", DOTNET_CLI_HOME: "/tmp",
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE: "1",
    DOTNET_CLI_TELEMETRY_OPTOUT: "1", NUGET_PACKAGES: "/tmp/.nuget/packages",
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE: "1",
  };
  const out = spawnSync(command, args, {
    cwd: "/workspace", env, timeout: maxMs, maxBuffer: 65536,
    encoding: "utf8", stdio: ["ignore", "pipe", "pipe"],
  });
  return {passed: out.status === 0 && !out.error,
          stdout: out.stdout || "", stderr: out.stderr || ""};
}
async function externalDenied() {
  return await new Promise(resolve => {
    let done = false;
    const conn = net.connect({host: "1.1.1.1", port: 443});
    const finish = value => {
      if (done) return;
      done = true;
      conn.destroy();
      resolve(value);
    };
    conn.on("connect", () => finish(false));
    conn.on("error", () => finish(true));
    setTimeout(() => finish(false), 1800);
  });
}
try {
  const status = fs.readFileSync("/proc/self/status", "utf8");
  const mount = fs.readFileSync("/proc/self/mountinfo", "utf8").split("\n")
    .find(line => line.split(" - ")[0].split(" ")[4] === "/");
  required.non_root = typeof process.getuid === "function" && process.getuid() !== 0;
  required.no_new_privileges = /^NoNewPrivs:\s*1\s*$/m.test(status);
  required.no_effective_capabilities = /^CapEff:\s*0+\s*$/m.test(status);
  required.root_read_only =
    (mount?.split(" - ")[0].split(" ")[5]?.split(",") || []).includes("ro");
  required.no_external_tcp = await externalDenied();
  for (const [key, name] of [["source_is_regular", "Program.cs"],
                              ["project_is_regular", "Repair.csproj"]]) {
    const item = fs.lstatSync("/workspace/" + name);
    required[key] = item.isFile() && !item.isSymbolicLink() && item.nlink === 1;
  }
  if (Object.entries(required).slice(0, 7).every(entry => entry[1] === true)) {
    fs.mkdirSync("/tmp/m0-empty-nuget-source", {recursive: true});
    const restore = runFixed("dotnet", [
      "restore", "Repair.csproj", "--source", "/tmp/m0-empty-nuget-source",
      "-p:NuGetAudit=false", "--disable-parallel", "-v:q"
    ], 90000);
    required.offline_restore_passed = restore.passed;
    if (restore.passed) {
      const build = runFixed("dotnet", [
        "build", "Repair.csproj", "--no-restore",
        "-c", "Release", "-p:UseSharedCompilation=false", "-v:q"
      ], 90000);
      required.offline_build_passed = build.passed;
      if (build.passed) {
        const result = runFixed("dotnet", [
          "/workspace/bin/Release/net10.0/Repair.dll"
        ], 12000);
        required.output_exact = result.passed &&
          result.stdout.replace(/\r\n/g, "\n") === "Total: 15\n";
      }
    }
  }
} catch (_) {
  // No shell fallback; fail closed without emitting source or build output.
}
console.log(JSON.stringify({checks: required}));
if (!Object.values(required).every(x => x === true)) process.exitCode = 2;
