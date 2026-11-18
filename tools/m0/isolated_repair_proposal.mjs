// M0.11 real OpenCode-to-local-Qwen single constrained repair proposal.
// Rootless Podman --network=none, no source mounts or host credentials.
// The broker replaces the request with frozen-source prompt and never emits tool calls.
import http from "node:http";
import net from "node:net";
import fs from "node:fs";
import {spawn} from "node:child_process";

const [model, outside, prompt] = process.argv.slice(1);
const TASK = prompt;
const MAX_REPLY = 131072;
const MAX_CHAT = 1024 * 1024;
const checks = {
  unprivileged: typeof process.getuid === "function" && process.getuid() !== 0,
  no_new_privileges: false,
  no_effective_capabilities: false,
  root_read_only: false,
  host_sentinel_hidden: !fs.existsSync(outside),
  outbound_tcp_denied: false,
  metadata_reached: false,
  model_profile_verified: false,
  cli_exited_successfully: false,
  one_inference_request: false,
  proposal_visible: false,
  wrong_show_model_denied: false,
  unsupported_routes_denied: false,
  no_unexpected_cli_route: true,
};
const routes = [];
let active = false;
let chatCount = 0;
try {
  const proc = fs.readFileSync("/proc/self/status", "utf8");
  checks.no_new_privileges = /^NoNewPrivs:\s*1\s*$/m.test(proc);
  checks.no_effective_capabilities = /^CapEff:\s*0+\s*$/m.test(proc);
  const root = fs.readFileSync("/proc/self/mountinfo", "utf8").split("\n")
    .find(line => line.split(" - ")[0].split(" ")[4] === "/");
  checks.root_read_only = (root?.split(" - ")[0].split(" ")[5]?.split(",") || []).includes("ro");
} catch (_) { /* fail closed */ }

function deny(res) {
  res.writeHead(403, {"Content-Type": "application/json"});
  res.end('{"error":"not permitted"}');
}
function collect(req, max) {
  return new Promise(resolve => {
    const parts = [];
    let bytes = 0;
    req.on("data", chunk => {
      bytes += chunk.length;
      if (bytes <= max) parts.push(chunk);
    });
    req.on("end", () => resolve(bytes <= max ? Buffer.concat(parts) : null));
    req.on("error", () => resolve(null));
  });
}
async function proxy(req, res, method, path, body) {
  return await new Promise(resolve => {
    const headers = body === null ? {"Connection": "close"} :
      {"Content-Type": "application/json",
       "Content-Length": String(body.length), "Connection": "close"};
    const outgoing = http.request({
      socketPath: "/run/codingassistant/ollama.sock", path, method, headers,
      timeout: 270000,
    }, upstream => {
      const chunks = [];
      let size = 0;
      upstream.on("data", chunk => {
        size += chunk.length;
        if (size > MAX_REPLY) outgoing.destroy(new Error("response budget"));
        else chunks.push(chunk);
      });
      upstream.on("end", () => {
        const data = Buffer.concat(chunks);
        if (upstream.statusCode === 200 && path !== "/v1/chat/completions") {
          try {
            const parsed = JSON.parse(data);
            if (path === "/api/tags" && parsed?.models?.length === 1 &&
                parsed.models[0]?.name === model && parsed.models[0]?.model === model) {
              checks.metadata_reached = true;
            }
            if (path === "/api/show" && Array.isArray(parsed?.capabilities) &&
                parsed.capabilities.includes("completion") &&
                parsed.capabilities.includes("tools")) {
              checks.model_profile_verified = true;
            }
          } catch (_) { /* invalid metadata not counted */ }
        }
        if (!res.headersSent) {
          res.writeHead(upstream.statusCode, {
            "Content-Type": upstream.headers["content-type"] || "application/json",
            "Content-Length": String(data.length), "Cache-Control": "no-store",
          });
        }
        res.end(data);
        resolve();
      });
    });
    outgoing.on("timeout", () => outgoing.destroy(new Error("broker timeout")));
    outgoing.on("error", () => {
      if (!res.headersSent) res.writeHead(502);
      res.end();
      resolve();
    });
    if (body !== null) outgoing.write(body);
    outgoing.end();
  });
}
const server = http.createServer(async (req, res) => {
  const method = req.method || "";
  const path = req.url || "";
  if (active && routes.length < 20)
    routes.push((method + " " + path.split("?")[0]).slice(0, 96));
  const len = req.headers["content-length"];
  const ct = req.headers["content-type"]?.split(";")[0].trim().toLowerCase();
  const isGet = method === "GET" && ["/api/tags", "/v1/models"].includes(path) &&
    !len && !req.headers["transfer-encoding"];
  const isShow = method === "POST" && path === "/api/show";
  const isChat = method === "POST" && path === "/v1/chat/completions";
  const max = isShow ? 256 : isChat ? MAX_CHAT : 0;
  const framed = (isShow || isChat) && ct === "application/json" &&
    typeof len === "string" && /^\d+$/.test(len) && Number(len) > 0 &&
    Number(len) <= max && !req.headers["transfer-encoding"];
  if (!isGet && !framed) {
    if (active) checks.no_unexpected_cli_route = false;
    req.resume();
    deny(res);
    return;
  }
  const body = framed ? await collect(req, max) : await collect(req, 0);
  if (body === null || (framed && body.length !== Number(len)) ||
      (isGet && body.length !== 0)) {
    if (active) checks.no_unexpected_cli_route = false;
    deny(res);
    return;
  }
  if (isShow || isChat) {
    try {
      const parsed = JSON.parse(body);
      if (!parsed || typeof parsed !== "object" || parsed.model !== model) {
        if (active) checks.no_unexpected_cli_route = false;
        deny(res);
        return;
      }
      if (isShow && (Object.keys(parsed).length !== 1 ||
                     parsed.model !== model)) {
        if (active) checks.no_unexpected_cli_route = false;
        deny(res);
        return;
      }
      if (isChat && (parsed.stream !== true || !Array.isArray(parsed.messages))) {
        if (active) checks.no_unexpected_cli_route = false;
        deny(res);
        return;
      }
    } catch (_) {
      if (active) checks.no_unexpected_cli_route = false;
      deny(res);
      return;
    }
  }
  if (isChat) {
    chatCount++;
    if (chatCount > 1) {
      if (active) checks.no_unexpected_cli_route = false;
      deny(res);
      return;
    }
  }
  await proxy(req, res, method, path, framed ? body : null);
});

async function externalDenied() {
  return await new Promise(resolve => {
    let settled = false;
    const sk = net.connect({host: "1.1.1.1", port: 443});
    const done = value => {
      if (settled) return;
      settled = true;
      sk.destroy();
      resolve(value);
    };
    sk.on("connect", () => done(false));
    sk.on("error", () => done(true));
    setTimeout(() => done(false), 2000);
  });
}
function clientRequest(port, method, path, data = null) {
  return new Promise(resolve => {
    const r = http.request({
      hostname: "127.0.0.1", port, path, method, timeout: 3000,
      headers: data === null ? {} : {
        "Content-Type": "application/json",
        "Content-Length": String(data.length),
      },
    }, response => {
      response.resume();
      response.on("end", () => resolve(response.statusCode));
    });
    r.on("error", () => resolve(0));
    if (data) r.write(data);
    r.end();
  });
}
function runOpenCode(port) {
  return new Promise(resolve => {
    const config = {
      "$schema": "https://opencode.ai/config.json",
      model: "ollama/" + model,
      providers: {ollama: {settings: {
        baseURL: "http://127.0.0.1:" + port + "/v1",
      }}},
      policies: [
        {action: "provider.use", resource: "*", effect: "deny"},
        {action: "provider.use", resource: "ollama", effect: "allow"},
      ],
      permissions: [
        {action: "shell", resource: "*", effect: "deny"},
        {action: "edit", resource: "*", effect: "deny"},
        {action: "write", resource: "*", effect: "deny"},
      ],
      snapshots: false,
    };
    const env = {
      PATH: "/usr/local/bin:/usr/bin:/bin", HOME: "/tmp",
      XDG_CONFIG_HOME: "/tmp/.config", XDG_DATA_HOME: "/tmp/.local/share",
      XDG_STATE_HOME: "/tmp/.local/state", XDG_CACHE_HOME: "/tmp/.cache",
      OPENCODE_DISABLE_AUTOUPDATE: "1", CI: "1", NO_COLOR: "1",
      OPENCODE_CONFIG_CONTENT: JSON.stringify(config),
    };
    const child = spawn("opencode", [
      "run", "--standalone", "--model", "ollama/" + model,
      "--title", "M0 repair proposal only", "--format", "default", TASK,
    ], {cwd: "/tmp", env, stdio: ["ignore", "pipe", "pipe"]});
    let stdout = "", stderrBytes = 0, killed = false;
    const kill = () => { killed = true; child.kill("SIGKILL"); };
    const timer = setTimeout(kill, 300000);
    child.stdout.on("data", chunk => {
      if (stdout.length + chunk.length > 65536) kill();
      else stdout += chunk.toString("utf8");
    });
    child.stderr.on("data", chunk => {
      stderrBytes += chunk.length;
      if (stderrBytes > 65536) kill();
    });
    child.on("error", () => { clearTimeout(timer); resolve({code: null, killed: true, stdout}); });
    child.on("close", code => { clearTimeout(timer); resolve({code, killed, stdout}); });
  });
}

try {
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const port = server.address().port;
  checks.outbound_tcp_denied = await externalDenied();
  checks.unsupported_routes_denied =
    await clientRequest(port, "GET", "/api/delete") === 403;
  const foreign = Buffer.from('{"model":"foreign:1"}');
  checks.wrong_show_model_denied =
    await clientRequest(port, "POST", "/api/show", foreign) === 403;
  active = true;
  const result = await runOpenCode(port);
  active = false;
  checks.cli_exited_successfully = result.code === 0 && !result.killed;
  checks.one_inference_request = chatCount === 1;
  checks.proposal_visible = result.stdout.includes("values.Sum()");
} catch (_) {
  // Fail closed; no raw response or provider output logged.
} finally {
  if (server.listening) await new Promise(resolve => server.close(resolve));
}
console.log(JSON.stringify({checks, observed_routes: routes}));
if (!Object.values(checks).every(Boolean)) process.exitCode = 2;
