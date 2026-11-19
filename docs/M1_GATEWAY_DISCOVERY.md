# M1.0 — Read-only Gateway/ACP integration discovery

**Current milestone status:** M0 **GREEN on the Dell at documented evidence assurance**;
M1 **YELLOW / NOT YET SECURITY-QUALIFIED**. Windows host qualification and
M1 end-to-end operational security are separate gates.

The original approved M1 architecture remains:
ASP.NET Core control plane → OpenClaw Gateway → ACP runtime → OpenCode
→ locally pinned Ollama. M0's rootless, no-network OpenCode runner and independent
evaluator stay reusable. Do **not** assume that OpenClaw's own tool restrictions
automatically sandbox a child ACP coding harness.

## First operational step: inventory, not installation

Run this on the Dell:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/discovery.py
```

The reviewed Python script invokes only installed binaries with `--version`:
Node, OpenClaw, OpenCode (host presence informational), and Podman. It
does **not** install software, launch the Gateway or ACP, load OpenClaw
configuration, read environment secrets, contact Ollama, run model code,
or change existing projects. Output contains version tokens and booleans,
not executable paths, provider keys or arbitrary command logs. Evidence is
stored in ignored `artifacts/m1/gateway-discovery.json`.

It may report `M1.0 PREREQUISITES_BLOCKED` if the OpenClaw executable
is absent or host Node is too old. This is a **useful discovery outcome**,
not a failure of the already-qualified M0 or permission to install an
unreviewed CLI. No M1 autonomous coding is enabled by `DISCOVERY_READY`.

### Runtime compatibility verified in current official docs (Nov 19, 2026)

OpenClaw supports Node 24.16.0+ on the 24.x line or Node 26.1.0+
on the 26.x line. **Node 24.22.1+ or 26.12.1+ is strongly recommended
for security**; Node 22 is unsupported. Node 27 development releases
are intentionally not eligible for this initial production Gateway
integration (a more restrictive project choice). The Dell previously
reported Node 22.22.1, so it is **expected** to report an incompatible
host runtime until independently remedied. We do **not** automatically
upgrade the host: a separately pinned rootless Gateway container with
a supported patched Node is a possible M1 implementation path that
avoids disrupting the existing M0 toolchain.

OpenClaw's packaged `@openclaw/acpx` plugin **embeds** the ACP runtime;
there is no requirement to install a standalone `acpx` binary.
The plugin and its Gateway state are **NOT_INSPECTED** by this first
version-only probe. Its harness target id for OpenCode is `opencode`.
Do not silently select Claude/Codex/cloud providers or configure a cloud
fallback. ACP sessions run without interactive terminal permission prompts;
noninteractive permissions and a distinct external OS sandbox must be
verified before any coding turn.

Official source references checked for M1 kickoff:
- https://docs.openclaw.ai/install/node
- https://docs.openclaw.ai/tools/acp-agents/quickstart
- https://docs.openclaw.ai/tools/acp-agents-setup
- https://docs.openclaw.ai/tools/acp-agents

## M1 staged acceptance (no owner design questionnaire)

1. **M1.0:** Version-only discovery and patched Node/OpenClaw packaging
   decision, with model/cloud calls disabled.
2. **M1.1:** Read-only Gateway/embedded ACP plugin/OpenCode harness
   discovery under an explicit control-plane vs execution-plane boundary.
   Verify the actual runtime protocol/version; use **no invented Gateway
   methods** and no writes to original repos.
3. **M1.2:** Read-only coding-plan request with canonical plan version/hash;
   owner approval must exactly match both before execution is permitted.
4. **M1.3:** Single approved worktree-scoped coding turn, independently
   OS-enforced child-process, file and network confinement, exact provider
   pin and explicit denial of external Git remotes.
5. **M1.4:** Ten adversarial security negative tests, durable run events,
   reconnect, cancellation and crash recovery/reconciliation. Every
   security failure disables autonomous execution on that OS.
6. **M1 exit:** Linux *and* Windows need separately validated runtime and
   security gates before cross-platform M1 GREEN. A Dell-only pass cannot
   be used to claim Windows is qualified.

Keep deferred pluggable skills design in the backlog; don't let untrusted
skill prompts or scripts bypass owner approval, exact file scope, or OS
enforcement. No .NET API server, SQLite database, Mission Control UI,
or distributed runner is introduced in M1.0.
