# Implementation plan and acceptance gates

Architecture v1.0 (2026-11-18), decisions 0–39 approved. Owner granted implementation permission and GitHub development operation permission November 18.

## M0 — Model and hardware feasibility (IN PROGRESS)

1. **Inventory:** OS, CPU/GPU/RAM/storage, Git, .NET, Node, Ollama, OpenCode/OpenClaw, model digests. Read-only `tools/m0/preflight.py`.
2. **Inference:** measure `tools/m0/inference_probe.py` tool-call correctness, latency and model identity on both hosts.
3. **OpenCode:** verify selected explicit Ollama model and tool usage, no silent provider fallbacks.
4. **Fixtures:** three standardized independent tasks: API feature, known C# repair, full-stack React + .NET.
5. **Benchmarks:** three repeats/task/model, identical starts, in a disposable **OS-isolated** environment. Preserve evidence and source-state hashes.
6. **Qualification:** >=2/3 successful per task and >=7/9 total; no unauthorized operations, false verified claims or unrecoverable state failure; independent safety review required.

No measured model qualifies yet. No M0 GREEN is authorized without all exit criteria.

## M1 — Secure orchestration

Validate real OpenClaw Gateway → ACP → OpenCode integration. Read-only planning; exact plan-version/hash owner approval; worktree-scoped coding; OS-enforced file/process/network restrictions including children; 10 required negative security tests; durable run state and crash/reconnect reconciliation. Linux and Windows independently gated.

## M2 — .NET 10 foundation

Minimal APIs in vertical slices, local single-owner auth, SQLite/EF Core, tasks, plans, approvals, immutable evidence, idempotency, concurrency, monitoring, reconciliation, SignalR and unit/integration/security tests.

## M3 — Mission Control

React/Vite/TypeScript UI for project/model selection, objective entry, plan review/approval, live progress, guidance/pause/cancel, diffs, tests, Git operations, reconnect and durable history.

Never introduce speculative microservices, Redis, Kubernetes, distributed runners, or an additional orchestration engine.

Milestone outcomes: GREEN, YELLOW, RED, BLOCKED; missing evidence never implies success.
