# CodingAssistant — current project handoff

## AET-8.3 continuation record (November 28, 2026)

Live tracker: [issue #216 — [AET] AET-8.3 Native Dispatch Integration and Qualification](https://github.com/dvdhtzr/CodingAssistant/issues/216). This section is the authoritative continuation state for AET-8.3 and supersedes older notes for that milestone only.

- **Current milestone:** AET-8.3 (E2–E5 integration, then F qualification). Branch `feature/aet8-native-recovery-completion` (ahead of `main` `1ce0ab7`; no PR).
- **Validated commit:** `e522e1e` (E1). Model-free validated on Linux + .NET 10.0.404: `CodingAssistant.Api` builds clean under warnings-as-errors; Phase 2 smoke **PASS 783** (= 786 on Windows; delta is the platform gate — `Program.cs` byte-identical to `48f2b12`); `node --test tests/test_m4_native_exact_cleanup.mjs` **28/28**.
- **Completed capabilities (model-free / compile only; NOT native-qualified):** exclusive owner lease + single-use dispatch ownership (`BeginNativeDispatch`, steps 1–3 with preview-before-lease / revalidate-inside ordering); durable reservation + launch claim; independent cleanup observation shared under ownership (`ObserveNativeCleanupUnderLeaseAsync`, E1); transactional result+observation provenance binding (`RecordObservedNativeResult`, requires `archiveSha256==independently-observed JournalSha256`); terminal-failure vs cleanup-debt separation; startup/ revocation interruption without silent replay.
- **E5 regression added this session:** closed generated-source authorization gate (`RequireExecutablePolicy` fails closed — previously untested) and native provenance rejection (`PILOT_NATIVE_RESULT_UNCLAIMED`). Validation commands: `.\Test-CodingAssistant.ps1 -Full` (authoritative) or `.\Validate-NativeCleanup.ps1` (fast subset).
- **Key architectural finding:** the *generated-source* product dispatch (lifecycle steps 4–10) is intentionally un-completable now — `RequireExecutablePolicy` is hard-closed and `windows_job_fixed_io.mjs` accepts only frozen controls. The demonstrable E2 native-ownership lifecycle is the **frozen-source qualification** (`PilotWindowsJobSmoke`, step F), which is **Windows-only**. A cloud/Linux session cannot execute or qualify it.
- **Outstanding work:** E2 remaining = bring the frozen native dispatch lifecycle under the exclusive owner lease on Windows (step F); E3/E4 native behaviors are exercised by F; remaining E5 checks (ownership retention, mismatched journal already covered, cancellation/interruption at native layer) land with F. Then open a PR at the integration boundary (owner merge authorization required).
- **Known risks/blockers:** native dispatch, Windows Job Object containment and cleanup recovery can only be qualified on the owner's Windows workstation; historical evidence is not current qualification; do not weaken the closed executable-policy gate; do not misrepresent frozen-control dispatch as generated-source qualification.
- **Exact next implementation task:** run step-F frozen-source native qualification on the Windows host (see issue #216 runbook), capturing current `WINDOWS_PILOT_JOB_OWNERSHIP_PASS` evidence; then run `.\Test-CodingAssistant.ps1 -Full` at the clean integration commit before requesting merge.

---

**Checkpoint: November 19, 2026 (updated after verified Dell M1.12 end-to-end coding PASS).** Read this before doing more work. This file is an operational checkpoint, not a replacement for `docs/IMPLEMENTATION_PLAN.md`, `docs/SECURITY.md`, or detailed append-only M0/M1 evidence records. Check live GitHub `main` and open PRs before relying on the commit information below.

## Goal and scope

Build a **local-first coding assistant**: .NET 10 Minimal API and React/TypeScript Mission Control (M2/M3), orchestrated through OpenClaw Gateway → ACPX → pinned OpenCode → local Ollama (M1), with approved Git worktrees, SQLite, SignalR, explicit plan/version/hash approvals, and independent OS containment. Linux Dell first; Windows independently qualified later. No cloud LLM fallback, no Azure spending, no unrelated repository changes, and no additional orchestration framework.

Contributors must follow `AGENTS.md`, `docs/IMPLEMENTATION_PLAN.md`, and `docs/SECURITY.md`. Use GitHub branches/PRs for repository changes; do not distribute ad hoc patch files. Keep existing local evidence under ignored `artifacts/` and do not solicit or publish secret-bearing logs. Respect the owner's approval rules for autonomous coding and remote Git operations.

## Real status (not inferred from mock tests)

| Gate | Assessment | Evidence boundary |
| --- | --- | --- |
| M0 — feasibility | **GREEN on Dell only** | Local `qwen3.5:4b`, OpenCode v2.3.0, rootless offline Podman: 9/9 frozen functional attempts; independently reviewed M0.18 evidence/acceptance. Does **not** qualify Windows or arbitrary autonomous operation. |
| Interactive MVP | **Available separately** | `tools/mvp/assist.py` can generate/review a proposal and apply only to an approved disposable copy. It calls local Ollama directly; **do not confuse it with the OpenClaw/ACP product architecture**. |
| M1 — secure orchestration | **YELLOW overall; M1.12 GREEN on Dell** | Real owner-approved Gateway → ACPX → OpenCode → Qwen turn completed, 6/6 digest-pinned host-verified local inference requests, 9 tool calls, 17 ACP events, and independent offline .NET evaluator `VERIFIED` on one disposable C# repair. The Gateway container was removed and all health checks passed. The broader durable-state, crash/reconnect, integrated negative testing and Windows gates remain open. |
| M1.11 — actual Gateway backend diagnostic | **GREEN on Dell** | Owner ran `tools/m1/gateway_backend_rpc_probe.py --prepare` after PR #56. Real Gateway health 3/3 HTTP 200; registered ACPX backend `doctor()` passed, ACP protocol v1 confirmed, disposable guest removed. Local ignored evidence: `artifacts/m1/gateway-backend-rpc-a1c64f120648.json`; derived local image ID `61f1a21dcdbe2f70181b8788641bee0f7fc9b249290ecacedeb793244e8d5eb6`. No coding session or model was attempted. |
| M2 — .NET 10 foundation | **IN PROGRESS; first control-plane slice** | Authenticated loopback-only Minimal API and private SQLite tasks / immutable plan versions / exact approval are implemented; real model-free HTTP/SQLite smoke tests run in CI. The API now also stores non-executable run intents and numbered SQLite events, exposes cursor-based replay and authenticated SignalR notifications, and can cancel a pending intent. It **does not invoke M1**, approve execution, cancel an active agent or provide Mission Control yet. See `docs/M2_CONTROL_PLANE.md` and issue #73. |
| M3 — React Mission Control | **NOT STARTED** | Do not claim implementation. |

See `docs/M0_STATUS.md`, `docs/M1_STATUS.md`, and `docs/M1_GATEWAY_DISCOVERY.md` for accumulated historical detail. Older sections retain intermediate BLOCKED/YELLOW states and should not override this checkpoint.

## Latest Dell verification — M1.12 GREEN (integrated coding, not overall M1)

On November 19, 2026, the owner explicitly approved a new exact
SHA256 plan (`0349c4327de288058ca556289e09839235db3e84bc699e06b1fbd0582dc5cd06`)
for the fixed disposable `02-build-repair` project under
`qwen3.5:4b` on main `191a7b0`. The actual OpenClaw Gateway,
ACPX and OpenCode coding path completed, with **6/6 host-verified**
local Ollama requests (**0 failed, 0 denied**), **17 ACP events**,
**9 tool calls**, **three healthy Gateway endpoints** and independently
verified offline .NET repair (`VERIFIED`). Disposable container
cleanup was confirmed. PID accounting: limit 256, peak 107, limit-hit
events 0; the peak crossed the previous 96 limit. The previous
abort-on-startup incidents were plausibly related to that old cap,
but prior failures lack comparable cgroup counters.

**Real Dell acceptance evidence:**
`artifacts/m1/m112-approved-coding-4a73cdebeebf.json` (local,
Git-ignored; not available on GitHub). The displayed run status
`GATEWAY_APPROVED_CODING_PASS` is verified by the owner's console
output, not independently reproduced on this GitHub connector.
This validates a single frozen disposable repair, not arbitrary real
repos or Windows and not the remaining M1 run-state/security gates.

## After M1.12: finish M1 in cohesive product slices

1. **M1.12 integrated coding is GREEN on Dell** for the fixed and owner-approved disposable C# task. Do **not** create more preflight-only milestones or rerun this already-passed slice without a regression reason. Preserve its pinned local model, exact approval, host-verified requests and independent evaluation.
2. Deliver **durable run state**, cancellation/pause, crash/reconnect reconciliation, idempotency, and integrated hostile-operation denial/negative tests in cohesive product-facing functionality. Evidence must survive process failure without relying on the guest's self-report. No arbitrary owner repo access until its separate containment and authorization tests pass.
3. Independently qualify Windows/WSL2 if development moves to the more powerful Windows workstation; Dell-specific results do not automatically transfer.

Then follow the approved sequential M2 (.NET API, SQLite, approval/observability, SignalR) and M3 (React Mission Control) plan. Prefer a meaningful end-to-end feature and acceptance criteria to more tiny discovery-only PRs. Never downgrade containment or authorization to make a diagnostic turn green.

## Operating guidance for the next chat

- Inspect the current GitHub tree, this handoff, milestone plan, security rules, most recent PRs and exact blocking evidence **before implementing**.
- Treat documentation of historical commands as history, not instructions to rerun every preflight.
- Explain what is **verified**, what is **only mocked/recorded**, and what is **unverified**.
- Preserve valid M0 functionality and do not modify unrelated projects.
- Avoid long questionnaires; make bounded implementation decisions from the approved architecture and bring the owner only genuine tradeoffs or necessary local runtime verification.
