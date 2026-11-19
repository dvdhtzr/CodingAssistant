// M1.12: no-network guest-side *loopback-only* bridge to the single host Unix socket.
// Only the four known OpenCode/Ollama endpoint methods are forwarded. No DNS,
// arbitrary URL, headers, outbound TCP, host HTTP or remote provider access.
import fs from "node:fs";
import http from "node:http";
import { spawn } from "node:child_process";

const MODEL = "qwen3.5:4b";
const SOCK = "/run/codingassistant/ollama.sock";
const PORT = 11434;
const MAX_CHAT = 262144;
const MAX_RESPONSE = 8 * 1024 * 1024;
if (!fs.statSync(SOCK).isSocket() ||
    !/^[a-f0-9]{64}$/.test(process.env.CA_M112_PLAN_HASH || "")) {
  process.exit(78);
}
const permissions = [
  {action: "*", resource: "*", effect: "deny"},
];
for (const file of ["api/Program.cs", "api/Repair.csproj"]) {
  permissions.push({action: "read", resource: file, effect: "allow"});
  permissions.push({action: "read", resource: "/workspace/" + file, effect: "allow"});
}
for (const file of ["api/Program.cs", "/workspace/api/Program.cs"]) {
  permissions.push({action: "edit", resource: file, effect: "allow"});
}
const opencodeConfig = {
  "$schema": "https://opencode.ai/config.json",
  model: "ollama/" + MODEL,
  providers: {ollama: {settings: {baseURL: "http://127.0.0.1:" + PORT + "/v1"}}},
  policies: [
    {action: "provider.use", resource: "*", effect: "deny"},
    {action: "provider.use", resource: "ollama", effect: "allow"},
  ],
  permissions, snapshots: false, formatter: false,
};
const childEnv = {
  ...process.env,
  OPENCODE_CONFIG_CONTENT: JSON.stringify(opencodeConfig),
  OPENCODE_DISABLE_AUTOUPDATE: "1",
  CI: "1",
  NO_COLOR: "1",
};
function reject(res) {
  res.writeHead(403, {"Content-Type": "application/json", "Connection": "close"});
  res.end('{"error":"route denied"}');
}
function collect(req, limit) {
  return new Promise(resolve => {
    const parts = []; let size = 0;
    req.on("data", part => {
      size += part.length;
      if (size > limit) { req.destroy(); resolve(null); }
      else parts.push(part);
    });
    req.once("end", () => resolve(Buffer.concat(parts)));
    req.once("error", () => resolve(null));
  });
}
async function proxy(req, res, pathname, method, body) {
  await new Promise(resolve => {
    const outgoing = http.request({
      socketPath: SOCK, path: pathname, method, timeout: 780000,
      headers: body ? {
        "Content-Type": "application/json",
        "Content-Length": String(body.length),
        "Connection": "close",
      } : {"Connection": "close"},
    }, response => {
      let size = 0;
      const chunks = [];
      response.on("data", part => {
        size += part.length;
        if (size > MAX_RESPONSE) outgoing.destroy(new Error("oversized"));
        else chunks.push(part);
      });
      response.once("end", () => {
        if (res.writableEnded) return resolve();
        const data = Buffer.concat(chunks);
        const type = String(response.headers["content-type"] || "application/json");
        res.writeHead(response.statusCode || 502, {
          "Content-Type": type.startsWith("text/event-stream")
            ? "text/event-stream" : "application/json",
          "Content-Length": String(data.length), "Cache-Control": "no-store",
          "Connection": "close",
        });
        res.end(data); resolve();
      });
    });
    outgoing.once("timeout", () => outgoing.destroy(new Error("timeout")));
    outgoing.once("error", () => {
      if (!res.headersSent) res.writeHead(502);
      if (!res.writableEnded) res.end();
      resolve();
    });
    if (body) outgoing.write(body);
    outgoing.end();
  });
}
const server = http.createServer(async (req, res) => {
  const method = req.method || "";
  const pathname = req.url || "";
  const meta = method === "GET" &&
    ["/api/tags", "/v1/models"].includes(pathname) &&
    !req.headers["content-length"] && !req.headers["transfer-encoding"];
  const show = method === "POST" && pathname === "/api/show";
  const chat = method === "POST" && pathname === "/v1/chat/completions";
  const length = req.headers["content-length"];
  const max = show ? 256 : chat ? MAX_CHAT : 0;
  const framed = (show || chat) &&
    req.headers["content-type"]?.split(";")[0].trim() === "application/json" &&
    typeof length === "string" && /^\d+$/.test(length) &&
    +length > 0 && +length <= max &&
    !req.headers["transfer-encoding"];
  if (!meta && !framed) {req.resume(); reject(res); return;}
  const body = await collect(req, framed ? max : 0);
  if (body === null || (framed && body.length !== +length) ||
      (meta && body.length !== 0)) {reject(res); return;}
  if (framed) {
    let parsed;
    try {parsed = JSON.parse(body);} catch {reject(res); return;}
    if (!parsed || parsed.model !== MODEL ||
        (show && Object.keys(parsed).length !== 1) ||
        (chat && (parsed.stream !== true || !Array.isArray(parsed.messages)))) {
      reject(res); return;
    }
  }
  await proxy(req, res, pathname, method, framed ? body : null);
});
let gateway = null;
function shutdown() {
  if (gateway && !gateway.killed) gateway.kill("SIGTERM");
  server.close();
}
process.on("SIGTERM", shutdown);
process.on("SIGINT", shutdown);
server.on("error", () => process.exit(78));
server.listen(PORT, "127.0.0.1", () => {
  gateway = spawn(process.execPath, [
    "/app/openclaw.mjs", "gateway", "--allow-unconfigured",
    "--bind", "loopback", "--port", "18789",
  ], {cwd: "/app", env: childEnv, stdio: ["ignore","ignore","ignore"]});
  gateway.on("error", () => process.exit(78));
  gateway.on("exit", code => {
    server.close();
    process.exitCode = code === 0 ? 0 : 78;
  });
});
