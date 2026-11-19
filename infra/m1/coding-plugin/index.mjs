// M1.12 single-turn Gateway method; never export arbitrary tool or prompt APIs.
// The only permitted project is the owner's freshly copied frozen C# repair fixture.
import { getAcpRuntimeBackend } from "openclaw/plugin-sdk/acp-runtime-backend";
import { classifyAcpSessionFailure } from "./safe-failure.mjs";

const METHOD = "codingassistant.coding.run";
const STATUS_METHOD = "codingassistant.coding.status";
const PHASES = new Set([
  "waiting", "approved", "backend_lookup", "session_initialize",
  "session_ready", "turn_start", "turn_stream", "turn_result",
  "session_close", "completed", "failed",
]);
const PROMPT = [
  "You are editing ONLY an isolated disposable .NET C# project at /workspace.",
  "Fix the compiler errors in api/Program.cs, keeping existing intended behavior.",
  "Modify api/Program.cs only. Do not modify api/Repair.csproj or create files.",
  "Do not use network, Git, package installers, credentials, or other directories.",
  "Use the local Ollama model to write the correction. Report when finished.",
].join(" ");
let claimed = false;
let phase = "waiting";
let failureAt = null;
let lastFailure = null;
let turnSucceeded = false;
function setPhase(next) {
  if (!PHASES.has(next)) throw new Error("Unexpected coding state");
  phase = next;
}

const plugin = {
  id: "codingassistant-m112",
  name: "Approved disposable ACP coding slice",
  description: "One fixed task with exact host-approved plan-hash admission",
  register(api) {
    api.registerGatewayMethod(METHOD, async ({ params, respond }) => {
      const hash = process.env.CA_M112_PLAN_HASH || "";
      if (claimed || !/^[a-f0-9]{64}$/.test(hash) ||
          !params || typeof params !== "object" ||
          Array.isArray(params) || Object.keys(params).length !== 1 ||
          params.planHash !== hash) {
        respond(false, undefined, {
          code: "INVALID_REQUEST", message: "Missing or spent exact owner approval",
        });
        return;
      }
      claimed = true; // atomic before first await; never re-run a second coding turn.
      setPhase("approved");
      let handle;
      let runtime;
      let terminal = false;
      let approved = false;
      let events = 0;
      let toolCalls = 0;
      let timeoutId;
      try {
        setPhase("backend_lookup");
        const backend = getAcpRuntimeBackend("acpx");
        runtime = backend?.runtime;
        if (backend?.id !== "acpx" || typeof runtime?.ensureSession !== "function" ||
            typeof runtime?.startTurn !== "function" || typeof runtime?.close !== "function") {
          throw new Error("ACP backend unavailable");
        }
        setPhase("session_initialize");
        handle = await runtime.ensureSession({
          sessionKey: "agent:main:ca-m112-" + hash.slice(0, 24),
          agentId: "main",
          agent: "opencode",
          mode: "oneshot",
          cwd: "/workspace",
          model: "ollama/qwen3.5:4b",
          modelExplicit: true,
        });
        if (handle.backend !== "acpx" || handle.cwd && handle.cwd !== "/workspace") {
          throw new Error("Wrong runtime/session ownership");
        }
        setPhase("session_ready");
        setPhase("turn_start");
        const turn = runtime.startTurn({
          handle,
          text: PROMPT,
          mode: "prompt",
          requestId: "ca-m112-" + hash.slice(0, 24),
          onElicitation: async () => ({ action: "cancel" }),
        });
        setPhase("turn_stream");
        timeoutId = setTimeout(() => {
          void turn.cancel({ reason: "approved-turn-timeout" }).catch(() => {});
        }, 840000);
        for await (const event of turn.events) {
          events++;
          if (events > 2500 || event?.type === "error") {
            await turn.cancel({ reason: "unexpected-runtime-events" });
            throw new Error("Unexpected ACP runtime event");
          }
          if (event?.type === "tool_call") toolCalls++;
        }
        setPhase("turn_result");
        const result = await turn.result;
        terminal = result?.status === "completed";
        if (!terminal) throw new Error("ACP coding turn did not complete");
        approved = true;
      } catch (error) {
        failureAt = phase;
        lastFailure = classifyAcpSessionFailure(error);
        approved = false;
      } finally {
        if (timeoutId) clearTimeout(timeoutId);
        if (handle && runtime) {
          setPhase("session_close");
          try {
            await runtime.close({
              handle, reason: "disposable-turn-finished", discardPersistentState: true,
            });
          } catch (error) {
            if (!failureAt) {
              failureAt = "session_close";
              lastFailure = classifyAcpSessionFailure(error);
            }
            approved = false;
          }
        }
      }
      turnSucceeded = approved;
      setPhase(approved ? "completed" : "failed");
      if (!approved) {
        respond(false, undefined, {
          code: "UNAVAILABLE", message: "Approved ACP coding turn did not complete",
        });
        return;
      }
      // The host independently verifies workspace hashes, real Qwen requests and tests.
      respond(true, {
        backend: "acpx",
        turnCompleted: true,
        promptSent: true,
        sessionCreated: true,
        modelVerifiedByHost: false,
        eventsObserved: events,
        toolCallsObserved: toolCalls,
        modelInferenceClaim: "host-broker-verification-required"
      }, undefined);
    }, { scope: "operator.write", profileAccess: "independent" });
    // Read-only and model-free: bounded, no prompts, paths, tokens or raw errors.
    // A failed coding RPC can be diagnosed *without* saving Gateway stderr.
    api.registerGatewayMethod(STATUS_METHOD, async ({ params, respond }) => {
      const hash = process.env.CA_M112_PLAN_HASH || "";
      if (!/^[a-f0-9]{64}$/.test(hash) || !params ||
          typeof params !== "object" || Array.isArray(params) ||
          Object.keys(params).length !== 1 || params.planHash !== hash) {
        respond(false, undefined, {
          code: "INVALID_REQUEST", message: "Exact owner plan required",
        });
        return;
      }
      respond(true, {
        phase, failureAt, claimed, turnSucceeded, lastFailure,
      }, undefined);
    }, { scope: "operator.read", profileAccess: "independent" });
  },
};
export default plugin;
