# Implementation plan and acceptance gates

Architecture v1.0 (2026-11-18), decisions 0–39 approved. Owner granted implementation permission and GitHub development operation permission November 18.

## M0 — Model and hardware feasibility (GREEN on Dell; Windows separately gated)

1. **Inventory:** OS, CPU/GPU/RAM/storage, Git, .NET, Node, Ollama, OpenCode/OpenClaw, model digests. Read-only `tools/m0/preflight.py`.
2. **Inference:** measure `tools/m0/inference_probe.py` tool-call correctness, latency and model identity on both hosts.
3. **OpenCode:** verify selected explicit Ollama model and tool usage, no silent provider fallbacks.
4. **Fixtures:** three standardized independent tasks: API feature, known C# repair, full-stack React + .NET.
5. **Benchmarks:** three repeats/task/model, identical starts, in a disposable **OS-isolated** environment. Preserve evidence and source-state hashes.
6. **Qualification:** >=2/3 successful per task and >=7/9 total; no unauthorized operations, false verified claims or unrecoverable state failure; independent safety review required.

**Dell M0 exit — GREEN (November 19, 2026).** Local `qwen3.5:4b` using pinned OpenCode and rootless/offline Podman achieved **9/9** original frozen functional verifications. The owner then ran independent M0.18 review against all nine saved workspaces: `EVIDENCE_REVIEW_PASS`, nine `EVIDENCE_PASS` results, and independently derived `QUALIFIED`. Raw historical benchmark results remain unchanged; the independent audit is an append-only sidecar. This is a **recorded-evidence-level qualification** and not a cryptographically attested execution transcript or an unconditional security guarantee. Windows-host M0 runtime acceptance was not demonstrated by Dell results and is separately gated. See `docs/M0_INDEPENDENT_EVIDENCE_AUDIT.md` and `docs/M0_STATUS.md`.

## M1 — Secure orchestration (IN PROGRESS: M1.0 read-only discovery only)

Validate real OpenClaw Gateway → ACP → OpenCode integration. Read-only planning; exact plan-version/hash owner approval; worktree-scoped coding; OS-enforced file/process/network restrictions including children; 10 required negative security tests; durable run state and crash/reconnect reconciliation. Linux and Windows independently gated.

## M2 — .NET 10 foundation

Minimal APIs in vertical slices, local single-owner auth, SQLite/EF Core, tasks, plans, approvals, immutable evidence, idempotency, concurrency, monitoring, reconciliation, SignalR and unit/integration/security tests.

## M3 — Mission Control

React/Vite/TypeScript UI for project/model selection, objective entry, plan review/approval, live progress, guidance/pause/cancel, diffs, tests, Git operations, reconnect and durable history.

Never introduce speculative microservices, Redis, Kubernetes, distributed runners, or an additional orchestration engine.

Milestone outcomes: GREEN, YELLOW, RED, BLOCKED; missing evidence never implies success.

## Deferred capability — pluggable engineering skills (owner request, 2026-11-19)

Preserve a future **versioned, pluggable skills** interface for engineering
workflows such as refactoring, code review, test creation and testing.
Investigate compatibility with commonly distributed external skills,
including Anthropic-style skill packages, when the core milestones are ready.
Treat externally sourced skill descriptions, prompts, scripts and tools as
**untrusted inputs**, never as authority to bypass exact owner approval,
scoped permissions, model/provider pinning or OS-enforced isolation.
Explicit install/review, provenance, declared capabilities, per-skill
approval and audit evidence will be mandatory. No skills loader or
third-party execution is authorized in M0. This is a backlog requirement,
not a change to approved M0–M3 acceptance criteria.
