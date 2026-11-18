// M0.8b-2a: inspect the real OpenCode 2.3.0 model catalog in rootless Podman.
// Run only 'opencode models'; never 'opencode run' or model-directed tools.
import http from "node:http";
import net from "node:net";
import fs from "node:fs";
import { spawn } from "node:child_process";

const [model, outside] = process.argv.slice(1);
const MAX_REPLY = 131072;
const routes = [];
let captureCliRoutes = false;
const checks = {
  unprivileged: typeof process.getuid === "function" && process.getuid() !== 0,
  no_new_privileges: false,
  no_effective_capabilities: false,
  root_read_only: false,
  host_sentinel_hidden: !fs.existsSync(outside),
  outbound_tcp_denied: false,
  metadata_reached: false,
  model_profile_verified: false,
  wrong_show_model_denied: false,
  cli_exited_successfully: false,
  exact_model_visible: false,
  only_local_provider: false,
  unsupported_routes_denied: false,
  no_unexpected_cli_route: true,
};

try {
  const status = fs.readFileSync("/proc/self/status", "utf8");
  checks.no_new_privileges = /^NoNewPrivs:\s*1\s*$/m.test(status);
  checks.no_effective_capabilities = /^CapEff:\s*0+\s*$/m.test(status);
  const mounts = fs.readFileSync("/proc/self/mountinfo", "utf8").split("\n");
  const root = mounts.find(line => line.split(" - ")[0].split(" ")[4] === "/");
  checks.root_read_only = (root?.split(" - ")[0].split(" ")[5]?.split(",") || []).includes("ro");
} catch (_) {
  // Fail closed.
}

function deny(response) {
  response.writeHead(403, { "Content-Type": "application/json" });
  response.end('{"error":"not permitted"}');
}

const server = http.createServer((incoming, outgoing) => {
  const method = incoming.method || "UNKNOWN";
  const path = incoming.url || "";
  const route = (method + " " + path.split("?")[0]).replace(/[^a-zA-Z0-9/_ .:-]/g, "_").slice(0, 96);
  const metadataGet = method === "GET" && (path === "/api/tags" || path === "/v1/models")
    && !incoming.headers["transfer-encoding"] && !incoming.headers["content-length"];
  const rawLength = incoming.headers["content-length"];
  const metadataShow = method === "POST" && path === "/api/show" &&
    incoming.headers["content-type"]?.split(";")[0].trim().toLowerCase() === "application/json" &&
    typeof rawLength === "string" && /^\d+$/.test(rawLength) &&
    Number(rawLength) > 0 && Number(rawLength) <= 256 &&
    !incoming.headers["transfer-encoding"];
  const allowed = metadataGet || metadataShow;
  if (captureCliRoutes) {
    if (routes.length < 16) routes.push(route);
    if (!allowed) checks.no_unexpected_cli_route = false;
  }
  if (!allowed) {
    incoming.resume();
    deny(outgoing);
    return;
  }

  const pieces = [];
  let bytes = 0;
  let exceeded = false;
  incoming.on("data", chunk => {
    bytes += chunk.length;
    if (bytes > 256) exceeded = true;
    else pieces.push(chunk);
  });
  incoming.on("end", () => {
    if (exceeded || (metadataGet && bytes !== 0) ||
        (metadataShow && bytes !== Number(rawLength))) {
      deny(outgoing);
      return;
    }
    const body = metadataShow ? Buffer.concat(pieces) : null;
    if (metadataShow) {
      try {
        const parsed = JSON.parse(body);
        if (!parsed || typeof parsed !== "object" || Array.isArray(parsed) ||
            Object.keys(parsed).length !== 1 || parsed.model !== model) {
          deny(outgoing);
          return;
        }
      } catch (_) {
        deny(outgoing);
        return;
      }
    }
    const req = http.request({
      socketPath: "/run/codingassistant/ollama.sock",
      method, path, timeout: 10000,
      headers: metadataShow ?
        { "Content-Type": "application/json", "Content-Length": String(body.length),
          "Connection": "close" } :
        { "Connection": "close" },
    }, (reply) => {
      let count = 0;
      const parts = [];
      reply.on("data", chunk => {
        count += chunk.length;
        if (count > MAX_REPLY) req.destroy(new Error("response budget"));
        else parts.push(chunk);
      });
      reply.on("end", () => {
        const data = Buffer.concat(parts);
        if (reply.statusCode === 200) {
          try {
            const listed = JSON.parse(data.toString("utf8"));
            const tags = path === "/api/tags" &&
              listed?.models?.length === 1 &&
              listed.models[0]?.name === model &&
              listed.models[0]?.model === model &&
              typeof listed.models[0]?.digest === "string" &&
              listed.models[0]?.digest.length > 0;
            const catalog = path === "/v1/models" &&
              listed?.data?.length === 1 && listed.data[0]?.id === model;
            const show = path === "/api/show" &&
              Array.isArray(listed?.capabilities) &&
              listed.capabilities.includes("completion") &&
              listed.capabilities.includes("tools");
            if (tags || catalog) checks.metadata_reached = true;
            if (show) checks.model_profile_verified = true;
          } catch (_) {
            // Malformed broker response never qualifies.
          }
        }
        outgoing.writeHead(reply.statusCode, {
          "Content-Type": "application/json",
          "Content-Length": String(data.length),
          "Cache-Control": "no-store",
        });
        outgoing.end(data);
      });
    });
    req.on("timeout", () => req.destroy(new Error("proxy timeout")));
    req.on("error", () => {
      if (!outgoing.headersSent) outgoing.writeHead(502);
      outgoing.end();
    });
    if (body !== null) req.write(body);
    req.end();
  });
});

async function externalDenied() {
  return await new Promise(resolve => {
    const socket = net.connect({ host: "1.1.1.1", port: 443 });
    let ended = false;
    const done = denied => {
      if (ended) return;
      ended = true;
      socket.destroy();
      resolve(denied);
    };
    socket.on("connect", () => done(false));
    socket.on("error", () => done(true));
    setTimeout(() => done(false), 2000);
  });
}

function runModels(port) {
  return new Promise(resolve => {
    const config = {
      "$schema": "https://opencode.ai/config.json",
      model: "ollama/" + model,
      providers: { ollama: { settings: { baseURL: "http://127.0.0.1:" + port + "/v1" } } },
      policies: [
        { action: "provider.use", resource: "*", effect: "deny" },
        { action: "provider.use", resource: "ollama", effect: "allow" },
      ],
      permissions: [
        { action: "shell", resource: "*", effect: "deny" },
        { action: "edit", resource: "*", effect: "deny" },
        { action: "write", resource: "*", effect: "deny" },
      ],
      snapshots: false,
    };
    const env = {
      PATH: "/usr/local/bin:/usr/bin:/bin", HOME: "/tmp",
      XDG_CONFIG_HOME: "/tmp/.config", XDG_DATA_HOME: "/tmp/.local/share",
      XDG_STATE_HOME: "/tmp/.local/state", XDG_CACHE_HOME: "/tmp/.cache",
      OPENCODE_DISABLE_AUTOUPDATE: "1",
      OPENCODE_CONFIG_CONTENT: JSON.stringify(config),
    };
    const child = spawn("opencode", ["models", "ollama"], {
      cwd: "/tmp", env, stdio: ["ignore", "pipe", "pipe"],
    });
    let stdout = "", stderrBytes = 0, killed = false;
    const timer = setTimeout(() => {
      killed = true;
      child.kill("SIGKILL");
    }, 75000);
    child.stdout.on("data", chunk => {
      if (stdout.length + chunk.length > 131072) {
        killed = true;
        child.kill("SIGKILL");
      } else stdout += chunk.toString("utf8");
    });
    child.stderr.on("data", chunk => {
      stderrBytes += chunk.length;
      if (stderrBytes > 131072) {
        killed = true;
        child.kill("SIGKILL");
      }
    });
    child.on("error", () => {
      clearTimeout(timer);
      resolve({ code: null, output: "", killed: true });
    });
    child.on("close", (code) => {
      clearTimeout(timer);
      resolve({ code, output: stdout, killed });
    });
  });
}

try {
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  checks.outbound_tcp_denied = await externalDenied();
  const denied = await new Promise(resolve => {
    http.get({ hostname: "127.0.0.1", port: server.address().port,
               path: "/api/delete", timeout: 3000 }, reply => {
      reply.resume();
      reply.on("end", () => resolve(reply.statusCode === 403));
    }).on("error", () => resolve(false));
  });
  checks.unsupported_routes_denied = denied;
  const wrongShow = await new Promise(resolve => {
    const body = Buffer.from(JSON.stringify({ model: "foreign-model:1" }));
    const req = http.request({
      hostname: "127.0.0.1", port: server.address().port,
      path: "/api/show", method: "POST", timeout: 3000,
      headers: { "Content-Type": "application/json",
                 "Content-Length": String(body.length) },
    }, reply => {
      reply.resume();
      reply.on("end", () => resolve(reply.statusCode === 403));
    });
    req.on("error", () => resolve(false));
    req.end(body);
  });
  checks.wrong_show_model_denied = wrongShow;
  captureCliRoutes = true;
  const outcome = await runModels(server.address().port);
  captureCliRoutes = false;
  checks.cli_exited_successfully = outcome.code === 0 && !outcome.killed;

  const lines = outcome.output.replace(/\x1b\[[0-9;]*[a-zA-Z]/g, "").split(/\r?\n/)
    .map(line => line.trim()).filter(Boolean);
  const identifiers = lines.filter(line =>
    /^[a-zA-Z0-9_.-]+\/[a-zA-Z0-9_.:/+-]+$/.test(line));
  checks.exact_model_visible = identifiers.includes("ollama/" + model);
  checks.only_local_provider = identifiers.length > 0 &&
    identifiers.every(line => line === "ollama/" + model);
} catch (_) {
  // Fail closed. Only fixed boolean results and endpoint routes are disclosed.
} finally {
  if (server.listening) await new Promise(resolve => server.close(resolve));
}
console.log(JSON.stringify({ checks, observed_routes: routes }));
if (!Object.values(checks).every(Boolean)) process.exitCode = 2;
