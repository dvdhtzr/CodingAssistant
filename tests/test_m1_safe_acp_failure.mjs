import test from "node:test";
import assert from "node:assert/strict";
import {
  classifyAcpSessionFailure, FAILURE_KINDS, FAILURE_CODES, MODEL_REASONS,
} from "../infra/m1/coding-plugin/safe-failure.mjs";

test("explicit model unsupported is classified without retaining private message", () => {
  const failure = classifyAcpSessionFailure({
    name: "RequestedModelUnsupportedError",
    code: "ACP_MODEL_UNSUPPORTED",
    reason: "unadvertised-model",
    message: "Sensitive string: internal-model-catalog secret",
  });
  assert.deepEqual(failure, {
    kind: "MODEL_UNADVERTISED", errorCode: "ACP_MODEL_UNSUPPORTED",
    modelReason: "unadvertised-model", exitCode: null,
    signal: null, systemCode: null,
  });
  assert.equal(JSON.stringify(failure).includes("internal-model"), false);
});
test("model missing capability is distinguishable from rejected selection", () => {
  const failure = classifyAcpSessionFailure({
    name: "RequestedModelUnsupportedError",
    code: "ACP_MODEL_UNSUPPORTED",
    reason: "missing-capability",
  });
  assert.equal(failure.kind, "MODEL_CAPABILITY_MISSING");
});
test("causal OpenCode process exit wins over outer generic ACP init code", () => {
  const failure = classifyAcpSessionFailure({
    name: "AcpRuntimeError", code: "ACP_SESSION_INIT_FAILED",
    cause: {
      name: "AgentStartupError", detailCode: "AGENT_STARTUP_FAILED",
      exitCode: 13, stderrSummary: "secret password and shell logs",
    },
  });
  assert.deepEqual(failure, {
    kind: "AGENT_STARTUP_FAILED", errorCode: "ACP_SESSION_INIT_FAILED",
    modelReason: null, exitCode: 13,
    signal: null, systemCode: null,
  });
  assert.equal(JSON.stringify(failure).includes("secret"), false);
});
test("OpenCode model catalog and service errors are safe fixed categories", () => {
  assert.equal(classifyAcpSessionFailure({
    code: -32603, message: "No models are available: /home/secret",
  }).kind, "MODEL_CATALOG_EMPTY");
  assert.equal(classifyAcpSessionFailure({
    code: -32603, message: "OpenCode server is unavailable",
  }).kind, "OPENCODE_SERVER_UNAVAILABLE");
  assert.equal(classifyAcpSessionFailure({
    code: -32602, message: "provider authentication required",
  }).kind, "PROVIDER_AUTH_REQUIRED");
});
test("process and filesystem typed outcomes retain no raw messages", () => {
  assert.equal(classifyAcpSessionFailure({code:"ENOENT",message:"/private/path"}).kind,
    "FILESYSTEM_MISSING");
  assert.equal(classifyAcpSessionFailure({
    detailCode:"AGENT_SPAWN_ENOENT", message:"/private",
  }).kind, "AGENT_SPAWN_ENOENT");
  assert.equal(classifyAcpSessionFailure({
    name:"TimeoutError",message:"/private",
  }).kind, "STARTUP_TIMEOUT");
});
test("startup SIGKILL and nested operating-system spawn code are bounded", () => {
  const error = {
    name: "AgentStartupError", detailCode: "AGENT_STARTUP_FAILED",
    exitCode: null, signal: "SIGKILL", stderrSummary: "PRIVATE: sensitive string",
    cause: { name: "Error", code: "EAGAIN", message: "hidden process path" },
  };
  const failure = classifyAcpSessionFailure(error);
  assert.deepEqual(failure, {
    kind: "AGENT_STARTUP_FAILED", errorCode: "AGENT_STARTUP_FAILED",
    modelReason: null, exitCode: null, signal: "SIGKILL",
    systemCode: "EAGAIN",
  });
  assert.equal(JSON.stringify(failure).includes("PRIVATE"), false);
});
test("unknown or malicious process signals and errno are never returned", () => {
  const failure = classifyAcpSessionFailure({
    name: "AgentStartupError", detailCode: "AGENT_STARTUP_FAILED",
    signal: "SIGMY_PRIVATE_PATH", cause: {
      code: "ECHILD_SECRET", message: "SECRETS",
    },
  });
  assert.equal(failure.signal, null);
  assert.equal(failure.systemCode, null);
  assert.equal(JSON.stringify(failure).includes("SECRET"), false);
});
test("unrecognized or recursive errors cannot disclose details or create new enums", () => {
  const hostile = {
    name: "VerySensitiveToken",
    code: "API_KEY_hunter2",
    reason: "secret",
    message: "password and source code",
    exitCode: -999,
  };
  hostile.cause = hostile;
  const failure = classifyAcpSessionFailure(hostile);
  assert.deepEqual(failure, {
    kind: "OTHER", errorCode: "NONE", modelReason: null, exitCode: null,
    signal: null, systemCode: null,
  });
  assert.equal(FAILURE_KINDS.includes(failure.kind), true);
  assert.equal(FAILURE_CODES.includes(failure.errorCode), true);
  assert.equal(MODEL_REASONS.includes(failure.modelReason), false);
  assert.equal(JSON.stringify(failure).includes("hunter2"), false);
});
