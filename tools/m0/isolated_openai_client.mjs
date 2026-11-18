// M0.8b-1 fixed Node 22 client for a rootless, --network=none Podman image.
// No OpenCode invocation, child processes, command execution, or writable host mounts.
import http from "node:http";
import net from "node:net";
import fs from "node:fs";

const [model, serializedRequest, outside] = process.argv.slice(1);
const socketPath = "/run/codingassistant/ollama.sock";
const MAX_BODY = 262144;
const checks = {
  unprivileged: typeof process.getuid === "function" && process.getuid() !== 0,
  root_read_only: false,
  host_sentinel_hidden: !fs.existsSync(outside),
  outbound_tcp_denied: false,
  metadata_pinned: false,
  wrong_model_denied: false,
  unsupported_path_denied: false,
  sse_tool_call: false,
};

try {
  fs.writeFileSync("/etc/codingassistant-m08b-write-denied", "forbidden");
} catch (_) {
  checks.root_read_only = true;
}

function request(options, body = null) {
  return new Promise((resolve, reject) => {
    const call = http.request({ timeout: 320000, ...options }, (reply) => {
      const parts = [];
      let bytes = 0;
      reply.on("data", (chunk) => {
        bytes += chunk.length;
        if (bytes > MAX_BODY) {
          call.destroy(new Error("Response budget exceeded"));
        } else {
          parts.push(chunk);
        }
      });
      reply.on("end", () =>
        resolve({ status: reply.statusCode, headers: reply.headers, body: Buffer.concat(parts) }));
      reply.on("error", reject);
    });
    call.on("timeout", () => call.destroy(new Error("Transport timeout")));
    call.on("error", reject);
    if (body !== null) call.write(body);
    call.end();
  });
}

function deny(res) {
  res.writeHead(403, { "Content-Type": "application/json" });
  res.end('{"error":"not permitted"}');
}

// Local-only, single-purpose loopback adapter. The HOST Unix broker independently
// rejects all arbitrary prompts/models and reconstructs its upstream request.
const server = http.createServer((req, res) => {
  const allowed = (req.method === "GET" && req.url === "/v1/models") ||
    (req.method === "POST" && req.url === "/v1/chat/completions");
  if (!allowed || req.headers["transfer-encoding"]) {
    deny(res);
    return;
  }
  const declared = req.headers["content-length"];
  const length = req.method === "GET" ? 0 : Number(declared);
  if (req.method === "POST" &&
      (!declared || !/^\d+$/.test(declared) || length < 1 || length > 4096)) {
    deny(res);
    return;
  }
  const pieces = [];
  let bytes = 0;
  req.on("data", (part) => {
    bytes += part.length;
    if (bytes > 4096) req.destroy();
    else pieces.push(part);
  });
  req.on("end", async () => {
    if (bytes !== length) {
      deny(res);
      return;
    }
    try {
      const upstream = await request({
        socketPath, method: req.method, path: req.url,
        headers: {
          "Content-Type": "application/json",
          "Content-Length": String(bytes),
          "Connection": "close",
        },
      }, req.method === "POST" ? Buffer.concat(pieces) : null);
      res.writeHead(upstream.status, {
        "Content-Type": upstream.headers["content-type"] || "application/json",
        "Content-Length": String(upstream.body.length),
        "Cache-Control": "no-store",
      });
      res.end(upstream.body);
    } catch (_) {
      res.writeHead(502);
      res.end();
    }
  });
});

function validateStream(body) {
  const lines = body.toString("utf8").split(/\r?\n/).map(s => s.trim()).filter(Boolean);
  if (!lines.length || lines.at(-1) !== "data: [DONE]") return false;
  let name = "", args = "", finish = null, calls = 0;
  for (const line of lines.slice(0, -1)) {
    if (!line.startsWith("data: ")) return false;
    const event = JSON.parse(line.slice(6));
    if (event.model !== model || !Array.isArray(event.choices) || event.choices.length !== 1) return false;
    const choice = event.choices[0];
    if (choice.index !== 0 || !choice.delta || typeof choice.delta !== "object") return false;
    if (choice.finish_reason !== null && choice.finish_reason !== undefined)
      finish = choice.finish_reason;
    for (const item of choice.delta.tool_calls || []) {
      if (item.index !== 0) return false;
      if (item.function?.name) {
        if (item.function.name !== "report_ready") return false;
        name = item.function.name;
        calls++;
      }
      args += item.function?.arguments || "";
    }
  }
  return finish === "tool_calls" && name === "report_ready" &&
    calls === 1 && JSON.stringify(JSON.parse(args)) === '{"status":"READY"}';
}

function noExternalTcp() {
  return new Promise((resolve) => {
    let settled = false;
    const socket = net.connect({ host: "1.1.1.1", port: 443 });
    const finish = (denied) => {
      if (!settled) {
        settled = true;
        socket.destroy();
        resolve(denied);
      }
    };
    socket.on("connect", () => finish(false));
    socket.on("error", () => finish(true));
    setTimeout(() => finish(false), 2000);
  });
}

try {
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const origin = { hostname: "127.0.0.1", port: server.address().port };
  checks.outbound_tcp_denied = await noExternalTcp();
  const models = await request({ ...origin, path: "/v1/models" });
  if (models.status === 200) {
    const data = JSON.parse(models.body);
    checks.metadata_pinned = data?.data?.length === 1 && data.data[0]?.id === model;
  }
  const malformed = JSON.parse(serializedRequest);
  malformed.model = "unapproved-model";
  const denied = await request({
    ...origin, method: "POST", path: "/v1/chat/completions",
    headers: { "Content-Type": "application/json" }
  }, Buffer.from(JSON.stringify(malformed)));
  checks.wrong_model_denied = denied.status === 403;

  const unknown = await request({ ...origin, path: "/api/delete" });
  checks.unsupported_path_denied = unknown.status === 403;

  const done = await request({
    ...origin, method: "POST", path: "/v1/chat/completions",
    headers: { "Content-Type": "application/json" }
  }, Buffer.from(serializedRequest));
  checks.sse_tool_call = done.status === 200 &&
    (done.headers["content-type"] || "").includes("text/event-stream") &&
    validateStream(done.body);
} catch (error) {
  // Only a short error category is included; never record prompts or model output.
  process.stderr.write("M0.8b probe error: " + String(error?.name || "Error") + "\n");
} finally {
  server.close();
}
console.log(JSON.stringify({ checks }, null, 0));
if (!Object.values(checks).every(Boolean)) process.exitCode = 2;
