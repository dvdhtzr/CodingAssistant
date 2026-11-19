# CodingAssistant — current project handoff

**Checkpoint: November 19, 2026.** Read this before doing more work. This file is an operational checkpoint, not a replacement for `docs/IMPLEMENTATION_PLAN.md`, `docs/SECURITY.md`, or detailed append-only M0/M1 evidence records. Check live GitHub `main` and open PRs before relying on the commit information below.

## Goal and scope

Build a **local-first coding assistant**: .NET 10 Minimal API and React/TypeScript Mission Control (M2/M3), orchestrated through OpenClaw Gateway → ACPX → pinned OpenCode → local Ollama (M1), with approved Git worktrees, SQLite, SignalR, explicit plan/version/hash approvals, and independent OS containment. Linux Dell first; Windows independently qualified later. No cloud LLM fallback, no Azure spending, no unrelated repository changes, and no additional orchestration framework.

Contributors must follow `AGENTS.md`, `docs/IMPLEMENTATION_PLAN.md`, and `docs/SECURITY.md`. Use GitHub branches/PRs for repository changes; do not distribute ad hoc patch files. Keep existing local evidence under ignored `artifacts/` and do not solicit or publish secret-bearing logs. Respect the owner's approval rules for autonomous coding and remote Git operations.

## Real status (not inferred from mock tests)

| Gate | Assessment | Evidence boundary |
| --- | --- | --- |
| M0 — feasibility | **GREEN on Dell only** | Local `qwen3.5:4b`, OpenCode v2.3.0, rootless offline Podman: 9/9 frozen functional attempts; independently reviewed M0.18 evidence/acceptance. Does **not** qualify Windows or arbitrary autonomous operation. |
| Interactive MVP | **Available separately** | `tools/mvp/assist.py` can generate/review a proposal and apply only to an approved disposable copy. It calls local Ollama directly; **do not confuse it with the OpenClaw/ACP product architecture**. |
| M1 — secure orchestration | **YELLOW, not accepted** | Real isolated Gateway health, ACPX registration/metadata, installed OpenCode, independent ACP initialize, 12/12 image-specific negative checks on Dell. A disposable writable-fixture probe is merged, but its independent owner acceptance is not clearly recorded in the canonical M1 status. No approved end-to-end Gateway-owned coding turn yet. |
| M1.11 — actual Gateway backend diagnostic | **NOT VERIFIED ON DELL after correction** | Old `openclaw agent --message '/acp doctor'` was blocked despite healthy Gateway. PR #56 (squash commit `34cfcc818a04369d74ddfbc0af61387e550db533`) replaced it with an explicit `operator.read` `codingassistant.acp.doctor` Gateway RPC in an offline, diagnostic-only derived image. Ubuntu and Windows **model-free CI passed**. This is NOT a live Gateway RPC PASS. |
| M2 — .NET 10 foundation | **NOT STARTED** | Do not claim implementation. |
| M3 — React Mission Control | **NOT STARTED** | Do not claim implementation. |

See `docs/M0_STATUS.md`, `docs/M1_STATUS.md`, and `docs/M1_GATEWAY_DISCOVERY.md` for accumulated historical detail. Older sections retain intermediate BLOCKED/YELLOW states and should not override this checkpoint.

## Immediate next operator verification — one command on Dell

After this checkpoint's documentation PR is merged, update the local checkout and exercise the *actual* Gateway-owned diagnostic against the owner's already-pinned local M1.7 image:

```bash
cd ~/CodingAssistant && git switch main && git pull --ff-only && python3 tools/m1/gateway_backend_rpc_probe.py --prepare
```

This prepares only a copy-only, offline, diagnostic-purpose image layer. It starts a disposable, rootless/no-network/non-root Gateway with no host source mounts, cloud keys or model prompts. Required success is **`M1.11 GATEWAY_OWNED_ACP_DOCTOR_PASS`**, actual protocol v1 response, and verified container cleanup. Failure is **BLOCKED**; use bounded/sanitized evidence, not unsafe log dumping. Never declare a PASS from mocked CI alone. The operator must run this on the Dell; GitHub CI does not have access to the pinned local images.

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
