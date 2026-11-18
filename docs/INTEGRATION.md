# External interfaces and feasibility notes — 2026-11-18

## Ollama + OpenCode

Verify the exact installed OpenCode version before using scripted flags and config. M0 expects local Ollama's /api/show and /api/tags endpoints and OpenAI-compatible /v1 inference. Model IDs are pinned per run (e.g. `ollama/qwen3.5:4b`); no cloud fallback is allowed. Do not expose a plain HTTP inference endpoint on untrusted networks.

M0's benchmark command/config is a candidate for empirical testing, not proof of OpenCode compatibility. A successful tool-call probe is necessary but insufficient for autonomous code edits.

Official references:
- https://opencode.ai/docs/models
- https://opencode.ai/docs/providers
- https://opencode.ai/docs/cli/
- https://opencode.ai/docs/permissions
- https://ollama.com/library/qwen3.5

## OpenClaw / ACP

The approved M1 path is ASP.NET Core → OpenClaw Gateway → ACP → OpenCode → Ollama. Validate installed OpenClaw/ACPX versions for allowed harnesses, workspace propagation, credentials, event delivery, session control, cancellation, interruption and restart reconciliation. Never invent Gateway methods.

External ACP harnesses are native child processes. OpenClaw's sandbox does not automatically restrict OpenCode. Real filesystem/process/network controls are mandatory.

References:
- https://docs.openclaw.ai/tools/acp-agents/quickstart
- https://docs.openclaw.ai/tools/acp-agents-setup

## App platform

.NET 10 ASP.NET Core Minimal APIs in vertical slices, EF Core/SQLite, React/Vite/TypeScript, SignalR, one-owner local authentication. Windows and Linux share source but require independent security and runtime acceptance. No .NET app server or Mission Control is implemented at M0.

References:
- https://dotnet.microsoft.com/download/dotnet/10.0
- https://learn.microsoft.com/dotnet/core/install/linux-ubuntu-install
