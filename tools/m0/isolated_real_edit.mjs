// M0.12 deterministic OpenCode native edit-tool contract.
// Run only inside a non-root, no-network, read-only-root container with a
// disposable two-file C# fixture mounted at /workspace. The model is a
// deterministic loopback mock: never real Ollama, cloud, or host source.
import http from "node:http";
import fs from "node:fs";
import net from "node:net";
import {spawn} from "node:child_process";

const [MODEL, OUTSIDE] = process.argv.slice(1);
const ID = "ollama/" + MODEL;
const SOURCE = "/workspace/Program.cs";
const PROJECT = "/workspace/Repair.csproj";
const BEFORE = fs.readFileSync(SOURCE, "utf8");
const PROJECT_BEFORE = fs.readFileSync(PROJECT);
const AFTER = BEFORE.replace("values.Summ()", "values.Sum()");
const TASK = "In the disposable C# project, use only the edit tool to replace values.Summ() with values.Sum() in Program.cs. Do not use shell, write, patch, web, or other tools.";
if (BEFORE === AFTER || BEFORE.split("values.Summ()").length !== 2) {
  throw new Error("Frozen repair fixture mismatch");
}
// The guest does not reach the host network. Local loopback is only a
// narrowly validated adapter to a private AF_UNIX socket and pinned broker.
const MAX_REPLY = 131072;
const MAX_CHAT = 1048576;
const stats = {tags: 0, show: 0, chat: 0, denied: 0,
  offered_edit: false, tool_result: false, task_seen: false, schema_valid: false};
const routes = [];
let active = false;

function deny(res) {
  res.writeHead(403, {"Content-Type": "application/json"});
  res.end('{"error":"not permitted"}');
}
function collect(req, cap) {
  return new Promise(resolve => {
    const chunks = [];
    let size = 0;
    req.on("data", chunk => {
      size += chunk.length;
      if (size <= cap) chunks.push(chunk);
    });
    req.on("end", () => resolve(size <= cap ? Buffer.concat(chunks) : null));
    req.on("error", () => resolve(null));
  });
}
async function proxy(req, res, method, path, body) {
  await new Promise(resolve => {
    const headers = body === null ? {"Connection": "close"} :
      {"Content-Type": "application/json", "Content-Length": String(body.length),
        "Connection": "close"};
    const upstream = http.request({
      socketPath: "/run/codingassistant/ollama.sock", method, path, headers,
      timeout: 270000,
    }, reply => {
      const parts = [];
      let size = 0;
      reply.on("data", chunk => {
        size += chunk.length;
        if (size > MAX_REPLY) upstream.destroy(new Error("size limit"));
        else parts.push(chunk);
      });
      reply.on("end", () => {
        const result = Buffer.concat(parts);
        if (reply.statusCode === 200) {
          if (path === "/api/tags") {
            try {
              const parsed = JSON.parse(result);
              if (parsed.models?.length === 1 && parsed.models[0].name === MODEL &&
                  parsed.models[0].model === MODEL && parsed.models[0].digest)
                stats.tags++;
            } catch (_) { /* invalid metadata never passes */ }
          }
          if (path === "/api/show") {
            try {
              const parsed = JSON.parse(result);
              if (parsed.capabilities?.includes("completion") &&
                  parsed.capabilities?.includes("tools")) stats.show++;
            } catch (_) { /* invalid metadata never passes */ }
          }
        }
        res.writeHead(reply.statusCode || 502, {
          "Content-Type": reply.headers["content-type"] || "application/json",
          "Content-Length": String(result.length), "Cache-Control": "no-store",
        });
        res.end(result);
        resolve();
      });
    });
    upstream.on("timeout", () => upstream.destroy(new Error("broker timeout")));
    upstream.on("error", () => {
      if (!res.headersSent) res.writeHead(502);
      res.end();
      resolve();
    });
    if (body) upstream.write(body);
    upstream.end();
  });
}
function textOf(message) {
  if (typeof message?.content === "string") return message.content;
  if (Array.isArray(message?.content))
    return message.content.filter(x => typeof x?.text === "string")
      .map(x => x.text).join("\n");
  return "";
}
const server = http.createServer(async (req, res) => {
  const path = req.url || "";
  const method = req.method || "";
  if (active && routes.length < 20) routes.push(method + " " + path);
  const length = req.headers["content-length"];
  const contentType = req.headers["content-type"]?.split(";")[0].trim().toLowerCase();
  const metadata = method === "GET" &&
    ["/api/tags", "/v1/models"].includes(path) &&
    !length && !req.headers["transfer-encoding"];
  const show = method === "POST" && path === "/api/show";
  const chat = method === "POST" && path === "/v1/chat/completions";
  const max = show ? 256 : chat ? MAX_CHAT : 0;
  const framed = (show || chat) && contentType === "application/json" &&
    typeof length === "string" && /^\d+$/.test(length) &&
    Number(length) > 0 && Number(length) <= max &&
    !req.headers["transfer-encoding"];
  if (!metadata && !framed) {
    if (active) stats.denied++;
    req.resume();
    deny(res);
    return;
  }
  const body = await collect(req, framed ? max : 0);
  if (body === null || (framed && body.length !== Number(length)) ||
      (metadata && body.length !== 0)) {
    if (active) stats.denied++;
    deny(res);
    return;
  }
  if (framed) {
    try {
      const parsed = JSON.parse(body);
      if (!parsed || Array.isArray(parsed) || parsed.model !== MODEL) {
        if (active) stats.denied++;
        deny(res);
        return;
      }
      if (show && Object.keys(parsed).length !== 1) {
        if (active) stats.denied++;
        deny(res);
        return;
      }
      if (chat) {
        if (parsed.stream !== true || !Array.isArray(parsed.messages) ||
            stats.chat >= 2) {
          if (active) stats.denied++;
          deny(res);
          return;
        }
        if (stats.chat === 0) {
          const users = parsed.messages.filter(m => m?.role === "user");
          stats.task_seen = users.length === 1 &&
            textOf(users[0]).includes(TASK);
          const edit = parsed.tools?.filter(t =>
            t?.type === "function" && t?.function?.name === "edit");
          stats.offered_edit = edit?.length === 1;
          const schema = edit?.[0]?.function?.parameters;
          stats.schema_valid = Boolean(schema &&
            ["path", "oldString", "newString"].every(key =>
              Object.hasOwn(schema.properties || {}, key) &&
              schema.required?.includes(key)));
          if (!stats.task_seen || !stats.schema_valid) {
            if (active) stats.denied++;
            deny(res);
            return;
          }
        } else {
          stats.tool_result = parsed.messages.some(m =>
            m?.role === "tool" && m.tool_call_id === "call_m013_exact_edit") &&
            fs.readFileSync(SOURCE, "utf8") === AFTER;
          if (!stats.tool_result) {
            if (active) stats.denied++;
            deny(res);
            return;
          }
        }
        stats.chat++;
      }
    } catch (_) {
      if (active) stats.denied++;
      deny(res);
      return;
    }
  }
  await proxy(req, res, method, path, framed ? body : null);
});
function clientRequest(port, method, path, data = null) {
  return new Promise(resolve => {
    const request = http.request({
      hostname: "127.0.0.1", port, method, path, timeout: 2500,
      headers: data === null ? {} : {
        "Content-Type": "application/json", "Content-Length": String(data.length),
      },
    }, response => {
      response.resume();
      response.on("end", () => resolve(response.statusCode));
    });
    request.on("error", () => resolve(0));
    if (data) request.write(data);
    request.end();
  });
}

async function tcpDenied() {
  return await new Promise(resolve => {
    const connection = net.connect({host: "1.1.1.1", port: 443});
    let ended = false;
    const finish = value => {
      if (ended) return;
      ended = true;
      connection.destroy();
      resolve(value);
    };
    connection.on("connect", () => finish(false));
    connection.on("error", () => finish(true));
    setTimeout(() => finish(false), 1500);
  });
}
async function runCli(port) {
  const config = {
    "$schema": "https://opencode.ai/config.json",
    model: ID,
    providers: {ollama: {settings: {baseURL: "http://127.0.0.1:" + port + "/v1"}}},
    policies: [
      {action: "provider.use", resource: "*", effect: "deny"},
      {action: "provider.use", resource: "ollama", effect: "allow"},
    ],
    // Deny by default. The one exception is an edit of the fixed fixture.
    // OpenCode permissions are defense in depth, never the OS boundary.
    permissions: [
      {action: "*", resource: "*", effect: "deny"},
      {action: "edit", resource: "Program.cs", effect: "allow"},
      {action: "edit", resource: "/workspace/Program.cs", effect: "allow"},
    ],
    snapshots: false,
    formatter: false,
  };
  const env = {
    PATH: "/usr/local/bin:/usr/bin:/bin", HOME: "/tmp",
    XDG_CONFIG_HOME: "/tmp/.config", XDG_DATA_HOME: "/tmp/.local/share",
    XDG_STATE_HOME: "/tmp/.local/state", XDG_CACHE_HOME: "/tmp/.cache",
    OPENCODE_DISABLE_AUTOUPDATE: "1", CI: "1", NO_COLOR: "1",
    OPENCODE_CONFIG_CONTENT: JSON.stringify(config),
  };
  return await new Promise(resolve => {
    const child = spawn("opencode", [
      "run", "--standalone", "--model", ID,
      "--title", "M0.13 pinned real edit", "--format", "default", TASK,
    ], {cwd: "/workspace", env, stdio: ["ignore", "pipe", "pipe"]});
    let stdout = "", stderr = "", killed = false;
    const stop = () => { killed = true; child.kill("SIGKILL"); };
    const timer = setTimeout(stop, 300000);
    child.stdout.on("data", data => {
      if (stdout.length + data.length > 32768) stop();
      else stdout += data.toString("utf8");
    });
    child.stderr.on("data", data => {
      if (stderr.length + data.length > 32768) stop();
      else stderr += data.toString("utf8");
    });
    child.on("error", error => {
      clearTimeout(timer); resolve({exit: null, killed: true, error: error.name});
    });
    child.on("close", exit => {
      clearTimeout(timer); resolve({exit, killed, error: null,
        complete: stdout.includes("EDIT COMPLETE"),
        stderr_tail: stderr.slice(-200).replace(/[\x00-\x1f\x7f]/g, " ")});
    });
  });
}
try {
  const proc = fs.readFileSync("/proc/self/status", "utf8");
  const root = fs.readFileSync("/proc/self/mountinfo", "utf8").split("\n")
    .find(line => line.split(" - ")[0].split(" ")[4] === "/");
  const checks = {
    non_root: process.getuid() !== 0,
    no_new_privileges: /^NoNewPrivs:\s*1\s*$/m.test(proc),
    zero_capabilities: /^CapEff:\s*0+\s*$/m.test(proc),
    read_only_root: (root?.split(" - ")[0].split(" ")[5]?.split(",") || []).includes("ro"),
    no_external_tcp: await tcpDenied(),
    host_sentinel_hidden: !fs.existsSync(OUTSIDE),
    no_git_mount: !fs.existsSync("/workspace/.git"),
  };
  if (Object.values(checks).some(x => !x)) throw new Error("isolation guard failed");
  await new Promise((resolve, reject) => {
    server.once("error", reject); server.listen(0, "127.0.0.1", resolve);
  });
  const port = server.address().port;
  checks.unsupported_routes_denied =
    await clientRequest(port, "GET", "/api/delete") === 403;
  const foreign = Buffer.from('{"model":"foreign:1"}');
  checks.wrong_model_denied =
    await clientRequest(port, "POST", "/api/show", foreign) === 403;
  active = true;
  const cli = await runCli(port);
  active = false;
  checks.cli_exited_successfully = cli.exit === 0 && !cli.killed && cli.complete;
  checks.metadata_pinned = stats.tags >= 1 && stats.show >= 1;
  checks.two_bounded_chat_requests = stats.chat === 2 && stats.denied === 0;
  checks.model_tool_call_executed = stats.tool_result && stats.offered_edit &&
    stats.schema_valid && stats.task_seen;
  checks.exact_source_edit = fs.readFileSync(SOURCE, "utf8") === AFTER;
  checks.project_unchanged = PROJECT_BEFORE.equals(fs.readFileSync(PROJECT));
  checks.no_extra_files = JSON.stringify(fs.readdirSync("/workspace").sort()) ===
    JSON.stringify(["Program.cs", "Repair.csproj"]);
  console.log(JSON.stringify({checks, observed_routes: routes}));
  if (!Object.values(checks).every(Boolean)) process.exitCode = 2;
} catch (error) {
  console.error("M0.13 REAL_OPENCODE_EDIT_BLOCKED",
    String(error?.name || "Error").slice(0, 100));
  process.exitCode = 2;
} finally {
  if (server.listening) await new Promise(resolve => server.close(resolve));
}
