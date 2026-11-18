// CI-only OpenCode 2.3.0 discovery contract.
// Runs the actual installed CLI against deterministic native Ollama metadata,
// with container networking disabled, no host source mounts, and no model calls.
import http from "node:http";
import { spawn } from "node:child_process";

const model = "qwen3.5:4b";
const routes = [];
const meta = {
  format: "gguf", family: "qwen3.5", families: ["qwen3.5"],
  parameter_size: "4B", quantization_level: "Q4_K_M",
};
const tags = { models: [{ name: model, model, digest: "d".repeat(64),
                           modified_at: "2026-11-18T00:00:00Z",
                           size: 2000000000, details: meta }] };
const show = {
  capabilities: ["completion", "tools"],
  details: meta,
  model_info: { "qwen3.5.context_length": 32768 },
};
let failed = false;

const server = http.createServer((req, res) => {
  const route = req.method + " " + req.url;
  routes.push(route);
  const send = (status, object) => {
    res.writeHead(status, { "Content-Type": "application/json" });
    res.end(JSON.stringify(object));
  };
  if (route === "GET /api/tags") {
    send(200, tags);
    return;
  }
  if (route !== "POST /api/show") {
    failed = true;
    req.resume();
    send(403, { error: "Unexpected discovery route" });
    return;
  }
  const parts = [];
  let size = 0;
  req.on("data", part => {
    size += part.length;
    if (size > 256) {
      failed = true;
      req.destroy();
    } else parts.push(part);
  });
  req.on("end", () => {
    try {
      const request = JSON.parse(Buffer.concat(parts).toString());
      if (Object.keys(request).length !== 1 || request.model !== model) {
        failed = true;
        send(403, { error: "Unexpected model profile request" });
        return;
      }
      send(200, show);
    } catch (_) {
      failed = true;
      send(403, { error: "Invalid profile body" });
    }
  });
});
const close = async () => {
  if (server.listening) await new Promise(resolve => server.close(resolve));
};

try {
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const port = server.address().port;
  const config = {
    "$schema": "https://opencode.ai/config.json",
    model: "ollama/" + model,
    providers: { ollama: { settings: {
      baseURL: "http://127.0.0.1:" + port + "/v1",
    } } },
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
  const result = await new Promise(resolve => {
    const child = spawn("opencode", ["models"], {
      cwd: "/tmp", env, stdio: ["ignore", "pipe", "pipe"],
    });
    let stdout = "", stderr = "", killed = false;
    const kill = () => { killed = true; child.kill("SIGKILL"); };
    const timer = setTimeout(kill, 75000);
    child.stdout.on("data", part => {
      if (stdout.length + part.length > 131072) kill();
      else stdout += part.toString("utf8");
    });
    child.stderr.on("data", part => {
      if (stderr.length + part.length > 131072) kill();
      else stderr += part.toString("utf8");
    });
    child.on("error", () => { clearTimeout(timer); resolve({ code: null, stdout, stderr, killed: true }); });
    child.on("close", code => { clearTimeout(timer); resolve({ code, stdout, stderr, killed }); });
  });
  const listed = result.stdout.replace(/\x1b\[[0-9;]*[a-zA-Z]/g, "")
    .split(/\r?\n/).map(x => x.trim()).filter(Boolean);
  const expected = "ollama/" + model;
  const local = listed.filter(id => id.startsWith("ollama/"));
  const pinned = local.length === 1 && local[0] === expected;
  if (result.killed || result.code !== 0 || !pinned || failed ||
      !routes.includes("GET /api/tags") || !routes.includes("POST /api/show") ||
      routes.some(r => r !== "GET /api/tags" && r !== "POST /api/show")) {
    process.stderr.write("OpenCode discovery contract FAIL: " +
      JSON.stringify({ exit_code: result.code, killed: result.killed,
                       only_pinned_ollama_model_visible: pinned,
                       routes, route_error: failed,
                       stderr_tail: result.stderr.slice(-800).replace(/[\x00-\x1f\x7f]/g, " "),
                       stdout_tail: result.stdout.slice(-300).replace(/[\x00-\x1f\x7f]/g, " ") }) + "\n");
    process.exitCode = 2;
  } else {
    console.log("OPENCODE_DISCOVERY_CONTRACT_PASS: exact model, GET tags, POST show");
  }
} catch (error) {
  process.stderr.write("OpenCode discovery contract error: " + String(error?.name || "Error") + "\n");
  process.exitCode = 2;
} finally {
  await close();
}
