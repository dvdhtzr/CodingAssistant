// Only fixed, source-reviewed diagnostic categories may cross the Gateway RPC.
// NEVER expose error.message, stack, stderr, env, paths, prompts or ACP payloads.
export const FAILURE_KINDS = Object.freeze([
  "OTHER", "MODEL_UNADVERTISED", "MODEL_CAPABILITY_MISSING",
  "MODEL_CATALOG_EMPTY", "PROVIDER_AUTH_REQUIRED", "OPENCODE_SERVER_UNAVAILABLE",
  "AGENT_SPAWN_ENOENT", "AGENT_SPAWN_FAILED", "AGENT_STARTUP_FAILED",
  "AGENT_DISCONNECTED", "SESSION_OWNER_INVALID", "SESSION_INIT_FAILED",
  "ACP_REQUEST_INVALID", "ACP_REQUEST_INTERNAL", "FILESYSTEM_DENIED",
  "FILESYSTEM_MISSING", "STARTUP_TIMEOUT", "BACKEND_UNAVAILABLE",
]);
export const FAILURE_CODES = Object.freeze([
  "NONE", "ACP_MODEL_UNSUPPORTED", "ACP_SESSION_INIT_FAILED",
  "ACP_BACKEND_UNAVAILABLE", "ACP_INVALID_RUNTIME_OPTION",
  "AGENT_STARTUP_FAILED", "AGENT_SPAWN_ENOENT", "AGENT_DISCONNECTED",
  "SESSION_OWNER_UNSUPPORTED", "SESSION_OWNER_MIGRATION_REQUIRED",
  "ENOENT", "EACCES", "EPERM", "ETIMEDOUT", "ECONNREFUSED", "EPIPE",
  "ACP_REQUEST_INVALID", "ACP_REQUEST_INTERNAL",
]);
export const MODEL_REASONS = Object.freeze([
  "missing-capability", "unadvertised-model",
]);
const safeKinds = new Set(FAILURE_KINDS);
const safeCodes = new Set(FAILURE_CODES);
const safeModelReasons = new Set(MODEL_REASONS);
const safeDetailCodes = new Set([
  "AGENT_STARTUP_FAILED", "AGENT_SPAWN_ENOENT", "AGENT_DISCONNECTED",
  "SESSION_OWNER_UNSUPPORTED", "SESSION_OWNER_MIGRATION_REQUIRED",
]);
const candidate = (value) => value && typeof value === "object" ? value : null;
const safeWord = (value, allowed) => typeof value === "string" && allowed.has(value)
  ? value : null;
function codeOf(error) {
  const value = error?.code;
  if (value === -32602) return "ACP_REQUEST_INVALID";
  if (value === -32603) return "ACP_REQUEST_INTERNAL";
  return safeWord(value, safeCodes) || null;
}
function classifiedKind(error, code, detail) {
  const name = typeof error?.name === "string" ? error.name : "";
  const message = typeof error?.message === "string"
    ? error.message.slice(0, 1024) : "";
  const reason = error?.reason;

  // A typed or exact fixed-text match, never the original message text.
  if (name === "RequestedModelUnsupportedError" ||
      code === "ACP_MODEL_UNSUPPORTED") {
    return reason === "missing-capability"
      ? "MODEL_CAPABILITY_MISSING" : "MODEL_UNADVERTISED";
  }
  if (detail === "SESSION_OWNER_UNSUPPORTED" ||
      detail === "SESSION_OWNER_MIGRATION_REQUIRED") return "SESSION_OWNER_INVALID";
  if (detail === "AGENT_SPAWN_ENOENT") return "AGENT_SPAWN_ENOENT";
  if (detail === "AGENT_STARTUP_FAILED" || name === "AgentStartupError") {
    return "AGENT_STARTUP_FAILED";
  }
  if (detail === "AGENT_DISCONNECTED" || name === "AgentDisconnectedError") {
    return "AGENT_DISCONNECTED";
  }
  if (name === "AgentSpawnError") return "AGENT_SPAWN_FAILED";
  if (/No models are available/i.test(message)) return "MODEL_CATALOG_EMPTY";
  if (/provider authentication required/i.test(message)) return "PROVIDER_AUTH_REQUIRED";
  if (/OpenCode server is unavailable/i.test(message)) return "OPENCODE_SERVER_UNAVAILABLE";
  if (/ACP agent exited before initialize completed/i.test(message)) {
    return "AGENT_STARTUP_FAILED";
  }
  if (code === "ENOENT") return "FILESYSTEM_MISSING";
  if (code === "EPERM" || code === "EACCES") return "FILESYSTEM_DENIED";
  if (code === "ETIMEDOUT" || name === "TimeoutError" ||
      /did not complete ACP initialization within \d+ms/.test(message)) {
    return "STARTUP_TIMEOUT";
  }
  if (code === "ACP_BACKEND_UNAVAILABLE") return "BACKEND_UNAVAILABLE";
  if (code === "ACP_SESSION_INIT_FAILED") return "SESSION_INIT_FAILED";
  if (code === "ACP_REQUEST_INVALID") return "ACP_REQUEST_INVALID";
  if (code === "ACP_REQUEST_INTERNAL") return "ACP_REQUEST_INTERNAL";
  return "OTHER";
}
const priority = {
  OTHER: 0, SESSION_INIT_FAILED: 1, ACP_REQUEST_INTERNAL: 2,
  ACP_REQUEST_INVALID: 2, STARTUP_TIMEOUT: 3, FILESYSTEM_MISSING: 4,
  FILESYSTEM_DENIED: 4, BACKEND_UNAVAILABLE: 4,
  MODEL_CATALOG_EMPTY: 5, PROVIDER_AUTH_REQUIRED: 5,
  OPENCODE_SERVER_UNAVAILABLE: 5, AGENT_SPAWN_FAILED: 5,
  AGENT_STARTUP_FAILED: 5, AGENT_DISCONNECTED: 5,
  AGENT_SPAWN_ENOENT: 6, MODEL_UNADVERTISED: 6,
  MODEL_CAPABILITY_MISSING: 6, SESSION_OWNER_INVALID: 6,
};
export function classifyAcpSessionFailure(value) {
  let kind = "OTHER";
  let errorCode = "NONE";
  let modelReason = null;
  let exitCode = null;
  const seen = new Set();
  let error = candidate(value);
  for (let depth = 0; error && depth < 4 && !seen.has(error); depth++) {
    seen.add(error);
    const code = codeOf(error);
    const detail = safeWord(error.detailCode, safeDetailCodes);
    const nextKind = classifiedKind(error, code, detail);
    if (safeKinds.has(nextKind) && priority[nextKind] > priority[kind]) {
      kind = nextKind;
    }
    if (errorCode === "NONE" && code) errorCode = code;
    if (errorCode === "NONE" && detail) errorCode = detail;
    if (!modelReason) modelReason = safeWord(error.reason, safeModelReasons);
    if (exitCode === null && Number.isInteger(error.exitCode) &&
        error.exitCode >= 0 && error.exitCode <= 255) exitCode = error.exitCode;
    error = candidate(error.cause);
  }
  return {
    kind: safeKinds.has(kind) ? kind : "OTHER",
    errorCode: safeCodes.has(errorCode) ? errorCode : "NONE",
    modelReason,
    exitCode,
  };
}
