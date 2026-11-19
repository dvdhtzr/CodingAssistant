// M1.12 single-turn Gateway method; never export arbitrary tool or prompt APIs.
// The only permitted project is the owner's freshly copied frozen C# repair fixture.
import { getAcpRuntimeBackend } from "openclaw/plugin-sdk/acp-runtime-backend";

const METHOD = "codingassistant.coding.run";
const PROMPT = [
  "You are editing ONLY an isolated disposable .NET C# project at /workspace.",
  "Fix the compiler errors in api/Program.cs, keeping existing intended behavior.",
  "Modify api/Program.cs only. Do not modify api/Repair.csproj or create files.",
  "Do not use network, Git, package installers, credentials, or other directories.",
  "Use the local Ollama model to write the correction. Report when finished.",
].join(" ");
let claimed = false;

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
      const backend = getAcpRuntimeBackend("acpx");
      const runtime = backend?.runtime;
      if (backend?.id !== "acpx" || typeof runtime?.ensureSession !== "function" ||
          typeof runtime?.startTurn !== "function" || typeof runtime?.close !== "function") {
        respond(false, undefined, {
          code: "UNAVAILABLE", message: "Registered ACPX backend unavailable",
        });
        return;
      }

      let handle;
      let terminal = false;
      let approved = false;
      let events = 0;
      let toolCalls = 0;
      let timeoutId;
      try {
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
        const turn = runtime.startTurn({
          handle,
          text: PROMPT,
          mode: "prompt",
          requestId: "ca-m112-" + hash.slice(0, 24),
          onElicitation: async () => ({ action: "cancel" }),
        });
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
        const result = await turn.result;
        terminal = result?.status === "completed";
        if (!terminal) throw new Error("ACP coding turn did not complete");
        approved = true;
      } catch {
        approved = false;
      } finally {
        if (timeoutId) clearTimeout(timeoutId);
        if (handle) {
          try {
            await runtime.close({
              handle, reason: "disposable-turn-finished", discardPersistentState: true,
            });
          } catch {
            approved = false;
          }
        }
      }
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
  },
};
export default plugin;
