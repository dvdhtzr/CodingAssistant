// M0.12 deterministic OpenCode native edit-tool contract.
// Run only inside a non-root, no-network, read-only-root container with a
// disposable two-file C# fixture mounted at /workspace. The model is a
// deterministic loopback mock: never real Ollama, cloud, or host source.
import http from "node:http";
import fs from "node:fs";
import net from "node:net";
import {spawn} from "node:child_process";

const MODEL = "qwen3.5:4b";
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
const details = {
  format: "gguf", family: "qwen3.5", families: ["qwen3.5"],
  parameter_size: "4B", quantization_level: "Q4_K_M",
};
const modelEntry = {
  name: MODEL, model: MODEL, modified_at: "2026-11-18T00:00:00Z",
  size: 2000000000, digest: "d".repeat(64), details,
};
const stats = {tags: 0, show: 0, chat: 0, denied: 0, offered_edit: false,
               tool_result: false, task_seen: false, tool_schema_valid: false};
const routes = [];
const LIMIT = 1048576;
let cli;

function json(res, code, value) {
  res.writeHead(code, {"Content-Type": "application/json", "Cache-Control": "no-store"});
  res.end(JSON.stringify(value));
}
async function bodyOf(req, cap) {
  return await new Promise(resolve => {
    const chunks = [];
    let size = 0;
    req.on("data", chunk => { size += chunk.length; if (size <= cap) chunks.push(chunk); });
    req.on("end", () => {
      if (size > cap) return resolve(null);
      try { resolve(JSON.parse(Buffer.concat(chunks).toString("utf8"))); }
      catch (_) { resolve(null); }
    });
    req.on("error", () => resolve(null));
  });
}
function textOf(message) {
  if (typeof message?.content === "string") return message.content;
  if (Array.isArray(message?.content))
    return message.content.filter(x => typeof x?.text === "string")
      .map(x => x.text).join("\n");
  return "";
}
function sse(res, events) {
  res.writeHead(200, {"Content-Type": "text/event-stream; charset=utf-8",
    "Cache-Control": "no-store", "Connection": "close"});
  for (const [delta, reason] of events) {
    const message = {
      id: "chatcmpl-m012-fixed", object: "chat.completion.chunk",
      created: 1, model: MODEL,
      choices: [{index: 0, delta, finish_reason: reason}],
    };
    res.write("data: " + JSON.stringify(message) + "\n\n");
  }
  res.end("data: [DONE]\n\n");
}
const server = http.createServer(async (req, res) => {
  const route = req.method + " " + req.url;
  if (routes.length < 16) routes.push(route);
  if (route === "GET /api/tags") {
    stats.tags++;
    json(res, 200, {models: [modelEntry]});
    return;
  }
  if (route === "POST /api/show") {
    const data = await bodyOf(req, 256);
    if (!data || Object.keys(data).length !== 1 || data.model !== MODEL) {
      stats.denied++;
      json(res, 403, {error: "model not pinned"});
      return;
    }
    stats.show++;
    json(res, 200, {capabilities: ["completion", "tools"], details,
      model_info: {"qwen3.5.context_length": 32768}});
    return;
  }
  if (route === "POST /v1/chat/completions") {
    const data = await bodyOf(req, LIMIT);
    if (!data || data.model !== MODEL || data.stream !== true ||
        !Array.isArray(data.messages) || stats.chat >= 2) {
      stats.denied++;
      json(res, 403, {error: "unexpected inference request"});
      return;
    }
    stats.chat++;
    if (stats.chat === 1) {
      const users = data.messages.filter(m => m?.role === "user");
      stats.task_seen = users.length === 1 && textOf(users[0]).includes(TASK);
      const edit = Array.isArray(data.tools) &&
        data.tools.find(t => t?.type === "function" && t?.function?.name === "edit");
      stats.offered_edit = Boolean(edit);
      const parameters = edit?.function?.parameters;
      const required = parameters?.required || [];
      const properties = parameters?.properties || {};
      stats.tool_schema_valid = Boolean(edit) &&
        ["path", "oldString", "newString"].every(name =>
          Object.hasOwn(properties, name) && required.includes(name));
      if (!stats.task_seen || !stats.tool_schema_valid) {
        stats.denied++;
        json(res, 403, {error: "missing bounded edit contract"});
        return;
      }
      const toolCall = {index: 0, id: "call_m012_exact_edit", type: "function",
        function: {name: "edit", arguments: JSON.stringify({
          path: SOURCE, oldString: "values.Summ()", newString: "values.Sum()",
        })}};
      sse(res, [
        [{role: "assistant"}, null],
        [{tool_calls: [toolCall]}, null],
        [{}, "tool_calls"],
      ]);
      return;
    }
    stats.tool_result = data.messages.some(m => m?.role === "tool" &&
      m?.tool_call_id === "call_m012_exact_edit") &&
      fs.readFileSync(SOURCE, "utf8") === AFTER;
    if (!stats.tool_result) {
      stats.denied++;
      json(res, 403, {error: "tool was not executed exactly"});
      return;
    }
    sse(res, [[{role: "assistant"}, null], [{content: "EDIT COMPLETE"}, null],
              [{}, "stop"]]);
    return;
  }
  stats.denied++;
  req.resume();
  json(res, 403, {error: "route forbidden"});
});

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
      "--title", "M0.12 bounded edit contract", "--format", "default", TASK,
    ], {cwd: "/workspace", env, stdio: ["ignore", "pipe", "pipe"]});
    let stdout = "", stderr = "", killed = false;
    const stop = () => { killed = true; child.kill("SIGKILL"); };
    const timer = setTimeout(stop, 120000);
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
    host_home_hidden: !fs.existsSync("/home/runner") && !fs.existsSync("/home/dave"),
    no_git_mount: !fs.existsSync("/workspace/.git"),
  };
  if (Object.values(checks).some(x => !x)) throw new Error("isolation checks failed");
  await new Promise((resolve, reject) => {
    server.once("error", reject); server.listen(0, "127.0.0.1", resolve);
  });
  cli = await runCli(server.address().port);
  const files = fs.readdirSync("/workspace").sort();
  const pass = cli.exit === 0 && !cli.killed && cli.complete &&
    stats.tags >= 1 && stats.show >= 1 && stats.chat === 2 &&
    stats.denied === 0 && stats.offered_edit && stats.tool_result &&
    stats.tool_schema_valid && stats.task_seen &&
    fs.readFileSync(SOURCE, "utf8") === AFTER &&
    PROJECT_BEFORE.equals(fs.readFileSync(PROJECT)) &&
    JSON.stringify(files) === JSON.stringify(["Program.cs", "Repair.csproj"]) &&
    Object.values(checks).every(Boolean);
  if (pass) console.log("M0.12 OPENCODE_DISPOSABLE_EDIT_CONTRACT_PASS");
  else {
    console.error("M0.12 OPENCODE_DISPOSABLE_EDIT_CONTRACT_BLOCKED", JSON.stringify({
      cli, stats, routes, checks, files,
      exact_edit: fs.readFileSync(SOURCE, "utf8") === AFTER,
      csproj_unchanged: PROJECT_BEFORE.equals(fs.readFileSync(PROJECT)),
    }));
    process.exitCode = 2;
  }
} catch (error) {
  console.error("M0.12 OPENCODE_DISPOSABLE_EDIT_CONTRACT_BLOCKED",
    String(error?.message || error?.name || "Error").slice(0, 180));
  process.exitCode = 2;
} finally {
  if (server.listening) await new Promise(resolve => server.close(resolve));
}
