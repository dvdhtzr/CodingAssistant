// M1.11-only: no-input Gateway RPC using the registered ACPX backend.
// This is a temporary diagnostic plugin, not an agent tool or runtime fallback.
import { getAcpRuntimeBackend } from "openclaw/plugin-sdk/acp-runtime-backend";

const plugin = {
  id: "codingassistant-acp-doctor",
  name: "CodingAssistant pinned ACP doctor diagnostic",
  description: "Gateway-owned, bounded ACP initialize diagnostic only",
  register(api) {
    api.registerGatewayMethod(
      "codingassistant.acp.doctor",
      async ({ params, respond }) => {
        if (!params || typeof params !== "object" ||
            Array.isArray(params) || Object.keys(params).length !== 0) {
          respond(false, undefined, {
            code: "INVALID_REQUEST",
            message: "Doctor diagnostic takes no arguments",
          });
          return;
        }
        const backend = getAcpRuntimeBackend("acpx");
        if (!backend || backend.id !== "acpx" ||
            typeof backend.runtime?.doctor !== "function") {
          respond(false, undefined, {
            code: "UNAVAILABLE",
            message: "Pinned ACPX backend not registered",
          });
          return;
        }
        try {
          const doctor = await backend.runtime.doctor();
          const details = Array.isArray(doctor.details) ? doctor.details : [];
          const versions = details.filter((item) =>
            typeof item === "string" &&
            /^protocolVersion=[A-Za-z0-9._-]{1,32}$/.test(item),
          );
          const version = versions.length === 1
            ? versions[0].slice("protocolVersion=".length)
            : null;
          if (doctor.ok !== true || doctor.message !== "embedded ACP runtime ready" ||
              version !== "1") {
            respond(false, undefined, {
              code: "UNAVAILABLE",
              message: "ACP initialize did not satisfy pinned protocol contract",
            });
            return;
          }
          respond(true, {
            backend: "acpx",
            runtimeDoctorOk: true,
            protocolVersion: "1",
            codingSessionCreated: false,
            promptSent: false,
          }, undefined);
        } catch {
          respond(false, undefined, {
            code: "UNAVAILABLE",
            message: "ACP diagnostic execution failed",
          });
        }
      },
      { scope: "operator.read", profileAccess: "independent" },
    );
  },
};
export default plugin;
