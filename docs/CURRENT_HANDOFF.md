# CodingAssistant — current project handoff

**Checkpoint: November 19, 2026 (updated after Dell M1.11 runtime acceptance).** Read this before doing more work. This file is an operational checkpoint, not a replacement for `docs/IMPLEMENTATION_PLAN.md`, `docs/SECURITY.md`, or detailed append-only M0/M1 evidence records. Check live GitHub `main` and open PRs before relying on the commit information below.

## Goal and scope

Build a **local-first coding assistant**: .NET 10 Minimal API and React/TypeScript Mission Control (M2/M3), orchestrated through OpenClaw Gateway → ACPX → pinned OpenCode → local Ollama (M1), with approved Git worktrees, SQLite, SignalR, explicit plan/version/hash approvals, and independent OS containment. Linux Dell first; Windows independently qualified later. No cloud LLM fallback, no Azure spending, no unrelated repository changes, and no additional orchestration framework.

Contributors must follow `AGENTS.md`, `docs/IMPLEMENTATION_PLAN.md`, and `docs/SECURITY.md`. Use GitHub branches/PRs for repository changes; do not distribute ad hoc patch files. Keep existing local evidence under ignored `artifacts/` and do not solicit or publish secret-bearing logs. Respect the owner's approval rules for autonomous coding and remote Git operations.

## Real status (not inferred from mock tests)

| Gate | Assessment | Evidence boundary |
| --- | --- | --- |
| M0 — feasibility | **GREEN on Dell only** | Local `qwen3.5:4b`, OpenCode v2.3.0, rootless offline Podman: 9/9 frozen functional attempts; independently reviewed M0.18 evidence/acceptance. Does **not** qualify Windows or arbitrary autonomous operation. |
| Interactive MVP | **Available separately** | `tools/mvp/assist.py` can generate/review a proposal and apply only to an approved disposable copy. It calls local Ollama directly; **do not confuse it with the OpenClaw/ACP product architecture**. |
| M1 — secure orchestration | **YELLOW, not accepted** | Real isolated Gateway health, ACPX registration/metadata, installed OpenCode, independent ACP initialize, 12/12 image-specific negative checks on Dell. A disposable writable-fixture probe is merged, but its independent owner acceptance is not clearly recorded in the canonical M1 status. No approved end-to-end Gateway-owned coding turn yet. |
| M1.11 — actual Gateway backend diagnostic | **GREEN on Dell** | Owner ran `tools/m1/gateway_backend_rpc_probe.py --prepare` after PR #56. Real Gateway health 3/3 HTTP 200; registered ACPX backend `doctor()` passed, ACP protocol v1 confirmed, disposable guest removed. Local ignored evidence: `artifacts/m1/gateway-backend-rpc-a1c64f120648.json`; derived local image ID `61f1a21dcdbe2f70181b8788641bee0f7fc9b249290ecacedeb793244e8d5eb6`. No coding session or model was attempted. |
| M2 — .NET 10 foundation | **NOT STARTED** | Do not claim implementation. |
| M3 — React Mission Control | **NOT STARTED** | Do not claim implementation. |

See `docs/M0_STATUS.md`, `docs/M1_STATUS.md`, and `docs/M1_GATEWAY_DISCOVERY.md` for accumulated historical detail. Older sections retain intermediate BLOCKED/YELLOW states and should not override this checkpoint.

## Latest Dell verification — M1.11 GREEN

The owner completed the registered Gateway/ACPX/OpenCode doctor on the Dell using the fixed, offline derived image. All three health checks returned 200; the real ACPX backend doctor reported protocol 1 and zero coding/model requests; the disposable guest was verified removed. This is **real runtime acceptance of the diagnostic path**, not approval of an end-to-end coding assignment. See the M1.11 row above. The next action is the approved disposable C# coding slice (M1.12), which must independently verify real model usage, exact owner consent and OS-enforced worktree containment.

## After M1.11: finish M1 in cohesive product slices

1. Prove a **real Gateway-owned, pinned local-Qwen coding request** in a fresh, approved disposable C# worktree through ACPX/OpenCode, mediated by a narrowly restricted Ollama broker. Explicit plan-version/hash approval, permitted files, blocked remote Git/network/secret/child-process escape, true model identity, independently checked code changes, and test results must be observable. No real user repo access until this is demonstrated.
2. Complete **durable run state**, cancellation/pause, crash/reconnect reconciliation, idempotency, and denial/negative tests for the **actual integrated execution path**. Record immutable evidence; do not rely on guest self-reporting alone.
3. Qualify **Windows independently** before claiming cross-platform security/runtime support; do not transpose the Dell result.

Then follow the approved sequential M2 (.NET API, SQLite, approval/observability, SignalR) and M3 (React Mission Control) plan. Prefer a meaningful end-to-end feature and acceptance criteria to more tiny discovery-only PRs. Never downgrade containment or authorization to make a diagnostic turn green.

## Operating guidance for the next chat

- Inspect the current GitHub tree, this handoff, milestone plan, security rules, most recent PRs and exact blocking evidence **before implementing**.
- Treat documentation of historical commands as history, not instructions to rerun every preflight.
- Explain what is **verified**, what is **only mocked/recorded**, and what is **unverified**.
- Preserve valid M0 functionality and do not modify unrelated projects.
- Avoid long questionnaires; make bounded implementation decisions from the approved architecture and bring the owner only genuine tradeoffs or necessary local runtime verification.
