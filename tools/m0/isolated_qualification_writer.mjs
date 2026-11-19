// M0.16: native OpenCode write-task execution in an isolated disposable copy.
// The model's chosen source bytes come from the pinned Unix-socket host broker.
// Do not add shell/read/write-all permissions or a network mount.
import fs from "node:fs";
import http from "node:http";
import net from "node:net";
import {spawn} from "node:child_process";

const [MODEL, TASK, PROMPT, SENTINEL] = process.argv.slice(1);
const PATHS = {
  "01-api-feature": ["api/Program.cs"],
  "02-build-repair": ["api/Program.cs"],
  "03-fullstack-feature": ["api/Program.cs", "web/src/TaskSummary.tsx"],
};
const allowed = PATHS[TASK];
const stats = {chat: 0, denied: 0, metadata: 0, show: 0};
const checks = {
  non_root: false, no_new_privileges: false, zero_capabilities: false,
  root_read_only: false, external_tcp_denied: false,
  sentinel_hidden: false, git_hidden: false, unauthorized_socket_hidden: false,
  unsupported_routes_denied: false, wrong_model_denied: false,
  metadata_pinned: false, chat_bounded: false,
  native_cli_complete: false,
};
const MAX_CHAT = 1048576;
const MAX_RESPONSE = 262144;
let active = false;

function textOf(message) {
  if (typeof message?.content === "string") return message.content;
  if (Array.isArray(message?.content))
    return message.content.filter(x => typeof x?.text === "string")
      .map(x => x.text).join("\n");
  return "";
}
function deny(response) {
  response.writeHead(403, {"Content-Type": "application/json"});
  response.end('{"error":"route denied"}');
}
function collect(req, limit) {
  return new Promise(resolve => {
    const parts = []; let length = 0;
    req.on("data", b => {
      length += b.length;
      if (length <= limit) parts.push(b);
    });
    req.on("end", () => resolve(length <= limit ? Buffer.concat(parts) : null));
    req.on("error", () => resolve(null));
  });
}
async function proxy(req, res, path, method, body) {
  return await new Promise(resolve => {
    const upstream = http.request({
      socketPath: "/run/codingassistant/ollama.sock", path, method,
      headers: body === null ? {"Connection": "close"} : {
        "Content-Type": "application/json", "Content-Length": String(body.length),
        "Connection": "close",
      }, timeout: 330000,
    }, reply => {
      const parts = []; let length = 0;
      reply.on("data", part => {
        length += part.length;
        if (length > MAX_RESPONSE) upstream.destroy(new Error("Response bound"));
        else parts.push(part);
      });
      reply.on("end", () => {
        const data = Buffer.concat(parts);
        if (reply.statusCode === 200) {
          try {
            const object = JSON.parse(data);
            if (path === "/api/tags" && object?.models?.length === 1 &&
                object.models[0]?.name === MODEL && object.models[0]?.digest)
              stats.metadata++;
            if (path === "/api/show" && object?.capabilities?.includes("tools") &&
                object.capabilities?.includes("completion"))
              stats.show++;
          } catch (_) { /* SSE intentionally not JSON */ }
        }
        res.writeHead(reply.statusCode || 502, {
          "Content-Type": reply.headers["content-type"] || "application/json",
          "Content-Length": String(data.length), "Cache-Control": "no-store",
        });
        res.end(data); resolve();
      });
    });
    upstream.once("timeout", () => upstream.destroy(new Error("Timeout")));
    upstream.once("error", () => {
      if (!res.headersSent) res.writeHead(502);
      res.end(); resolve();
    });
    if (body !== null) upstream.write(body);
    upstream.end();
  });
}
const server = http.createServer(async (req, res) => {
  const method = req.method || "";
  const path = req.url || "";
  const len = req.headers["content-length"];
  const media = req.headers["content-type"]?.split(";")[0].trim().toLowerCase();
  const meta = method === "GET" && ["/api/tags", "/v1/models"].includes(path) &&
    !len && !req.headers["transfer-encoding"];
  const show = method === "POST" && path === "/api/show";
  const chat = method === "POST" && path === "/v1/chat/completions";
  const maximum = show ? 256 : chat ? MAX_CHAT : 0;
  const framed = (show || chat) && media === "application/json" &&
    typeof len === "string" && /^\d+$/.test(len) &&
    Number(len) > 0 && Number(len) <= maximum &&
    !req.headers["transfer-encoding"];
  if ((!meta && !framed) || (active && chat && stats.chat >= allowed.length + 1)) {
    if (active) stats.denied++;
    req.resume(); deny(res); return;
  }
  const body = await collect(req, framed ? maximum : 0);
  if (body === null || (framed && body.length !== Number(len)) ||
      (meta && body.length !== 0)) {
    if (active) stats.denied++;
    deny(res); return;
  }
  if (framed) {
    try {
      const request = JSON.parse(body);
      if (!request || request.model !== MODEL ||
          (show && Object.keys(request).length !== 1) ||
          (chat && (request.stream !== true ||
                    !Array.isArray(request.messages)))) {
        if (active) stats.denied++;
        deny(res); return;
      }
      if (chat) {
        const users = request.messages.filter(m => m?.role === "user");
        const native = request.tools?.filter(t =>
          t?.type === "function" && t?.function?.name === "write");
        const schema = native?.[0]?.function?.parameters;
        if (users.length !== 1 || !textOf(users[0]).includes(PROMPT) ||
            native?.length !== 1 ||
            !["filePath", "content"].every(key =>
              schema?.required?.includes(key) &&
              Object.hasOwn(schema?.properties || {}, key))) {
          if (active) stats.denied++;
          deny(res); return;
        }
        stats.chat++;
      }
    } catch (_) {
      if (active) stats.denied++;
      deny(res); return;
    }
  }
  await proxy(req, res, path, method, framed ? body : null);
});
function requestLocal(port, path, method, object = null) {
  return new Promise(resolve => {
    const body = object === null ? null : Buffer.from(JSON.stringify(object));
    const outgoing = http.request({
      hostname: "127.0.0.1", port, path, method,
      timeout: 2000,
      headers: body === null ? {} : {
        "Content-Type": "application/json", "Content-Length": String(body.length),
      },
    }, response => {
      response.resume(); response.on("end", () => resolve(response.statusCode));
    });
    outgoing.on("error", () => resolve(0));
    if (body !== null) outgoing.write(body);
    outgoing.end();
  });
}
function noExternalTcp() {
  return new Promise(resolve => {
    let done = false;
    const socket = net.connect({host: "1.1.1.1", port: 443});
    const close = value => {
      if (done) return;
      done = true; socket.destroy(); resolve(value);
    };
    socket.once("connect", () => close(false));
    socket.once("error", () => close(true));
    setTimeout(() => close(false), 1600);
  });
}
function runCli(port) {
  const rules = [{action: "*", resource: "*", effect: "deny"}];
  for (const file of allowed) {
    rules.push({action: "write", resource: file, effect: "allow"});
    rules.push({action: "write", resource: "/workspace/" + file, effect: "allow"});
  }
  const config = {
    "$schema": "https://opencode.ai/config.json",
    model: "ollama/" + MODEL,
    providers: {ollama: {settings: {
      baseURL: "http://127.0.0.1:" + port + "/v1",
    }}},
    policies: [
      {action: "provider.use", resource: "*", effect: "deny"},
      {action: "provider.use", resource: "ollama", effect: "allow"},
    ],
    permissions: rules,
    snapshots: false, formatter: false,
  };
  const env = {
    PATH: "/usr/local/bin:/usr/bin:/bin", HOME: "/tmp",
    XDG_CONFIG_HOME: "/tmp/.config", XDG_DATA_HOME: "/tmp/.local/share",
    XDG_STATE_HOME: "/tmp/.local/state", XDG_CACHE_HOME: "/tmp/.cache",
    OPENCODE_DISABLE_AUTOUPDATE: "1", CI: "1", NO_COLOR: "1",
    OPENCODE_CONFIG_CONTENT: JSON.stringify(config),
  };
  return new Promise(resolve => {
    const cli = spawn("opencode", [
      "run", "--standalone", "--model", "ollama/" + MODEL,
      "--title", "M0.16 frozen coding qualification", "--format", "default",
      PROMPT,
    ], {cwd: "/workspace", env, stdio: ["ignore", "pipe", "pipe"]});
    let stdout = "", stderr = "", timedOut = false;
    const kill = () => {timedOut = true; cli.kill("SIGKILL");};
    const timer = setTimeout(kill, 660000);
    cli.stdout.on("data", data => {
      if (stdout.length + data.length > 131072) kill();
      else stdout += data.toString("utf8");
    });
    cli.stderr.on("data", data => {
      if (stderr.length + data.length > 131072) kill();
      else stderr += data.toString("utf8");
    });
    cli.on("error", e => {
      clearTimeout(timer);
      resolve({exit: null, timedOut: true, error: e.name});
    });
    cli.on("close", exit => {
      clearTimeout(timer);
      resolve({exit, timedOut,
        complete: stdout.includes("SOURCE WRITES COMPLETE; NOT VERIFIED"),
        // Only bounded sanitized tails, never trust terminal controls.
        stdoutTail: stdout.slice(-2000).replace(/[\x00-\x1f\x7f]/g, " "),
        stderrTail: stderr.slice(-500).replace(/[\x00-\x1f\x7f]/g, " "),
      });
    });
  });
}
try {
  if (!allowed || typeof PROMPT !== "string" || !PROMPT.length ||
      typeof SENTINEL !== "string" || !SENTINEL.startsWith("/"))
    throw new Error("Invalid approved task inputs");
  const proc = fs.readFileSync("/proc/self/status", "utf8");
  const root = fs.readFileSync("/proc/self/mountinfo", "utf8").split("\n")
    .find(line => line.split(" - ")[0].split(" ")[4] === "/");
  checks.non_root = process.getuid() !== 0;
  checks.no_new_privileges = /^NoNewPrivs:\s*1\s*$/m.test(proc);
  checks.zero_capabilities = /^CapEff:\s*0+\s*$/m.test(proc);
  checks.root_read_only =
    (root?.split(" - ")[0].split(" ")[5]?.split(",") || []).includes("ro");
  checks.external_tcp_denied = await noExternalTcp();
  checks.sentinel_hidden = !fs.existsSync(SENTINEL);
  checks.git_hidden = !fs.existsSync("/workspace/.git");
  checks.unauthorized_socket_hidden = !fs.existsSync("/var/run/docker.sock");
  if (!Object.values(checks).slice(0, 8).every(Boolean))
    throw new Error("OS isolation failed");
  await new Promise((resolve, reject) => {
    server.once("error", reject); server.listen(0, "127.0.0.1", resolve);
  });
  const port = server.address().port;
  checks.unsupported_routes_denied =
    await requestLocal(port, "/api/delete", "GET") === 403;
  checks.wrong_model_denied =
    await requestLocal(port, "/api/show", "POST", {model: "other:1"}) === 403;
  active = true;
  const cli = await runCli(port);
  active = false;
  checks.metadata_pinned = stats.metadata >= 1 && stats.show >= 1;
  checks.chat_bounded = stats.chat <= allowed.length + 1 && stats.denied === 0;
  checks.native_cli_complete = cli.exit === 0 && !cli.timedOut && cli.complete;
  console.log(JSON.stringify({task: TASK, checks, cli,
    chat_requests: stats.chat}));
  if (!Object.values(checks).every(Boolean)) process.exitCode = 2;
} catch (e) {
  console.log(JSON.stringify({task: TASK, checks,
    reason: String(e?.name || "Error")}));
  process.exitCode = 2;
} finally {
  if (server.listening)
    await new Promise(resolve => server.close(resolve));
}
