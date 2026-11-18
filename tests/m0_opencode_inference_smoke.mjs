// M0.8b-2b: actual pinned OpenCode inference-wire CI contract.
// Run with --network none and no host volumes. The model is a deterministic
// mock returning one TEXT answer. This test never calls Ollama or runs tools.
import http from "node:http";
import fs from "node:fs";
import { spawn } from "node:child_process";

const MODEL = "qwen3.5:4b";
const ID = "ollama/" + MODEL;
const TASK = "Reply with the single word READY. Do not use any tools.";
const routes = [];
const stats = {tags: 0, show: 0, inference: 0, wrong: 0, request_model_pinned: false,
               saw_task: false, streamed: false, generated_tool_calls: false};
const MAX_REQUEST = 1048576;
const details = {
  format: "gguf", family: "qwen3.5", families: ["qwen3.5"],
  parameter_size: "4B", quantization_level: "Q4_K_M",
};
const modelEntry = {
  name: MODEL, model: MODEL, modified_at: "2026-11-18T00:00:00Z",
  size: 2000000000, digest: "d".repeat(64), details,
};
function sendJson(res, code, data) {
  res.writeHead(code, {"Content-Type": "application/json", "Cache-Control": "no-store"});
  res.end(JSON.stringify(data));
}
function parseBody(req, limit) {
  return new Promise(resolve => {
    const pieces = [];
    let size = 0;
    req.on("data", chunk => {
      size += chunk.length;
      if (size <= limit) pieces.push(chunk);
    });
    req.on("end", () => {
      if (size > limit) { resolve(null); return; }
      try { resolve(JSON.parse(Buffer.concat(pieces).toString("utf8"))); }
      catch (_) { resolve(null); }
    });
    req.on("error", () => resolve(null));
  });
}
const server = http.createServer(async (req, res) => {
  const route = req.method + " " + (req.url || "");
  if (routes.length < 20) routes.push(route);
  if (route === "GET /api/tags") {
    stats.tags++;
    sendJson(res, 200, {models: [modelEntry]});
    return;
  }
  if (route === "POST /api/show") {
    const body = await parseBody(req, 256);
    if (!body || Object.keys(body).length !== 1 || body.model !== MODEL) {
      stats.wrong++;
      sendJson(res, 403, {error: "only pinned model"});
      return;
    }
    stats.show++;
    sendJson(res, 200, {capabilities: ["completion", "tools"], details,
      model_info: {"qwen3.5.context_length": 32768}});
    return;
  }
  if (route === "POST /v1/chat/completions") {
    const body = await parseBody(req, MAX_REQUEST);
    if (!body || typeof body !== "object" || Array.isArray(body) ||
        body.model !== MODEL || body.stream !== true || !Array.isArray(body.messages)) {
      stats.wrong++;
      sendJson(res, 403, {error: "invalid pinned OpenAI request"});
      return;
    }
    stats.inference++;
    stats.request_model_pinned = true;
    stats.streamed = body.stream === true;
    // Never record or publish the complete prompts or model-suggested tools.
    stats.saw_task = body.messages.some(m =>
      typeof m?.content === "string" && m.content.includes(TASK) ||
      Array.isArray(m?.content) && m.content.some(p =>
        typeof p?.text === "string" && p.text.includes(TASK)));
    const chunk = (delta, finish_reason = null) => ({
      id: "chatcmpl-m08b2b-fixed", object: "chat.completion.chunk",
      created: 1, model: MODEL,
      choices: [{index: 0, delta, finish_reason}],
    });
    res.writeHead(200, {"Content-Type": "text/event-stream; charset=utf-8",
      "Cache-Control": "no-store", "Connection": "close"});
    // The fixed mock returns only content, never tool calls.
    for (const event of [chunk({role: "assistant"}), chunk({content: "READY"}),
                         chunk({}, "stop")]) {
      res.write("data: " + JSON.stringify(event) + "\n\n");
    }
    res.end("data: [DONE]\n\n");
    return;
  }
  stats.wrong++;
  req.resume();
  sendJson(res, 403, {error: "endpoint forbidden"});
});

let cli;
try {
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const port = server.address().port;
  const config = {
    "$schema": "https://opencode.ai/config.json",
    model: ID,
    providers: {ollama: {settings: {baseURL: "http://127.0.0.1:" + port + "/v1"}}},
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
    OPENCODE_DISABLE_AUTOUPDATE: "1",
    OPENCODE_CONFIG_CONTENT: JSON.stringify(config),
    CI: "1", NO_COLOR: "1",
  };
  cli = await new Promise(resolve => {
    const child = spawn("opencode", ["run", "--standalone", "--model", ID,
                                    "--format", "default", TASK], {
      cwd: "/tmp", env, stdio: ["ignore", "pipe", "pipe"],
    });
    let stdout = "", stderr = "", killed = false;
    const stop = () => { killed = true; child.kill("SIGKILL"); };
    const timer = setTimeout(stop, 90000);
    child.stdout.on("data", part => {
      if (stdout.length + part.length > 65536) stop();
      else stdout += part.toString("utf8");
    });
    child.stderr.on("data", part => {
      if (stderr.length + part.length > 65536) stop();
      else stderr += part.toString("utf8");
    });
    child.on("error", error => {
      clearTimeout(timer);
      resolve({exit: null, stdout, stderr, killed: true, error: error.name});
    });
    child.on("close", exit => {
      clearTimeout(timer);
      resolve({exit, stdout, stderr, killed, error: null});
    });
  });
  const text = cli.stdout.replace(/\x1b\[[0-9;]*[a-zA-Z]/g, "");
  const pass = cli.exit === 0 && !cli.killed && stats.tags >= 1 &&
    stats.show >= 1 && stats.inference === 1 && stats.wrong === 0 &&
    stats.request_model_pinned && stats.streamed && stats.saw_task &&
    text.includes("READY");
  if (pass) {
    console.log("OPENCODE_INFERENCE_WIRE_CONTRACT_PASS: pinned Ollama, streaming text, no tools");
  } else {
    // Emit only route labels and bounded diagnostics; never raw model prompts.
    console.error("OPENCODE_INFERENCE_WIRE_CONTRACT_BLOCKED", JSON.stringify({
      exit: cli.exit, killed: cli.killed, error: cli.error, routes, stats,
      output_has_READY: text.includes("READY"),
      stderr_tail: cli.stderr.slice(-450).replace(/[\x00-\x1f\x7f]/g, " "),
    }));
    process.exitCode = 2;
  }
} catch (e) {
  console.error("OPENCODE_INFERENCE_WIRE_CONTRACT_BLOCKED", String(e?.name || "Error"));
  process.exitCode = 2;
} finally {
  if (server.listening) await new Promise(resolve => server.close(resolve));
}
