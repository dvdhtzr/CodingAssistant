# CodingAssistant — Development Plan v2.0

**Approved:** November 20, 2026  
**Scope:** Evolve the *working* local-first project generator into a coordinated Microsoft Agent Framework engineering team.  
**Status:** Phase 0 baseline protection GREEN with qualified evidence below; Phase 1 Product Owner in progress. No later phase has passed its acceptance gate.  
**Origin:** Owner-approved comprehensive handoff of November 20, 2026.

## Approved architecture extension — November 20, 2026

The human owner **approved** [Architecture Extension A1: Portable Skills, Runtime Profiles and Verified Artifacts](ARCHITECTURE_EXTENSION_A1.md). This is the target design for skills from vetted sources, selected model expertise, language-agnostic runtime capability profiles, disposable isolated build/test attempts, independent verification, bounded repair and truthful ZIP status. **Approval is architectural; no new executable capability is enabled.**

Maintain the six-phase ordering. Phase 1 Product Owner live-model quality remains **YELLOW** pending complete evidence (the owner exercised the Calculation Journal interview and ZIP path but the resulting program failed functional tests). In Phase 2 define and persist approved stack/architecture choices and **non-executable** runtime descriptors; in Phase 3 integrate verified Microsoft Agent Framework skills; in Phase 4 implement and independently qualify the Static Web sandbox/build/test/repair path first; in Phase 5 visualize verifiable results. Existing security issue #76 and task-execution issue #92 remain mandatory release gates. The frozen cross-file failure [#107](https://github.com/dvdhtzr/CodingAssistant/issues/107) is the initial regression target.

## Relationship to the original implementation plan

This is the **authoritative product-development sequence for new v2 work**. [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) remains the preserved historical M0–M3 architecture, qualification evidence and security-gate record. Do not overwrite that history, erase M0/M1 evidence or infer that a new feature relaxes security. [SECURITY.md](SECURITY.md), the current implementation, and release blockers [#76](https://github.com/dvdhtzr/CodingAssistant/issues/76) and [#92](https://github.com/dvdhtzr/CodingAssistant/issues/92) continue to constrain executable coding.

Our known working baseline: authenticated .NET 10 API, private SQLite, React/TypeScript Mission Control, local Ollama interview and specification, explicit specification approval, source-*text* generation and downloadable ZIP. Separate fixed-scope isolated .NET coding evaluation exists, but **source generation is not compilation or independent verification**. Windows/Linux support is mandatory; independent Linux qualification never substitutes for Windows execution qualification.

## Rules across all phases

1. **Phase order: 0 → 1 → 2 → 3 → 4 → 5.** Close each acceptance gate with evidence before moving to the next. Keep ordinary nonblocking deficiencies in tracked GitHub debt, not as invented gates.
2. **GitHub-only changes:** inspect `main`, branch, implement, test, PR, check CI, merge, verify `main`. No manual local source patches. The owner has authorized ordinary development PRs and merges; *product* execution permissions remain separate.
3. **Local-first inference:** explicitly selected Ollama model only. No mandatory hosted models, silent fallback or cloud spending.
4. **Human authority:** distinguish model recommendations, assumptions, deferrals, approved decisions, specification approval, and *separate* executable scope approval.
5. **Trust boundary:** MAF orchestrates reasoning/agent handoffs; existing reviewed OpenClaw/ACPX/OpenCode and sandbox infrastructure owns any permitted executable coding. Planning agents receive no shell or filesystem tools by default.
6. **Traceability:** record exact source/version/model, handoffs, decisions, approvals, durable events and independent test evidence. No invented progress, passing test or executable capability.
7. **Portability and efficiency:** .NET 10, React/TypeScript/Vite, SQLite, local Ollama; Windows and Linux; bounded deterministic, sequential collaboration before optional concurrent designs.

## Phase 0 — Baseline Protection (GREEN — protected baseline)

**Objective:** Make the already-working objective → interview → specification → approval → file planning → source generation → ZIP download journey hard to regress.

**Required work:**
- Inspect actual code for task creation, interview turns and persistence, specification version/hash and approval, file planning and generation, ZIP packaging and download.
- Retain and strengthen model-independent, authenticated real-HTTP/SQLite regression tests; run on Linux **and** Windows CI.
- Verify approval boundaries; reject wrong/stale hashes and revoked decisions, and test downloaded archive integrity and owner-scoped access.
- Keep explicit, separately identified real Ollama acceptance for interactive behavior. Model-free tests do **not** prove local inference quality.
- Preserve the original M0/M1 security and execution qualifications without re-running them gratuitously.

**Current baseline paths:** `src/CodingAssistant.Api/ApiHost.cs`, `InterviewStore.cs`, `LocalInterviewModel.cs`, `ProjectDraftCoordinator.cs`, `ProjectDraftStore.cs`, `LocalProjectDraftModel.cs`, `src/CodingAssistant.Web/src/InterviewStudio.tsx`, `ProjectDraftPanel.tsx`, `tests/CodingAssistant.Api.Smoke/Program.cs`, `.github/workflows/m0-tests.yml`.

**Repeatable checks (from a clean checkout):**

```sh
dotnet run --configuration Release --project tests/CodingAssistant.Api.Smoke/CodingAssistant.Api.Smoke.csproj
```

The existing GitHub Actions `M0 Model-Free Checks` workflow executes this real API smoke suite on `ubuntu-latest` and `windows-latest`, alongside Python, Node and React build checks. Observe the **actual PR workflow result**, not just the existence of a workflow file.

**Owner live acceptance:** With locally installed Ollama and the standard application launcher (or Windows development host), create a genuinely new objective; exchange multiple context-aware answers; inspect a draft spec; approve its current version/hash; generate source files; download/open the ZIP. Record model and host and separately distinguish source generation from actual build/tests. Live inference or Windows execution qualification is **not** asserted by model-free CI.

**Gate:** Existing CI checks pass; meaningful objective-to-ZIP regression coverage and independent archive/access checks pass; specification approval is enforced; existing saved records/working behavior are preserved; verification procedure is documented; any required live owner acceptance is recorded. Only then mark Phase 0 **GREEN**. Otherwise **YELLOW**, **RED**, or **BLOCKED** with a specific reason.

**Deliverable:** Protected, verified baseline on `main`; a recorded gate report.

**Phase 0 gate record, November 20, 2026:** [PR #103](https://github.com/dvdhtzr/CodingAssistant/pull/103) passed Linux and Windows model-free GitHub Actions and security checks before merge to `main` commit `50084cbf870c2d2aba90abb5a31aea93218e9fe3`. The real HTTP/SQLite smoke suite covers interview, version/hash approval, source-draft events and persistence, ZIP byte/SHA integrity, authentication and cross-task access. The owner separately reported that the earlier local-Ollama baseline completed objective → interview → approved spec → source ZIP on November 20. **That is owner-observed prior-version evidence**, not an independently repeated run on this merge; the host, exact model and transcript were not recorded. No live-model quality, generated-source compilation, Windows isolated execution or independent security qualification is claimed. Phase 0 is accepted as *regression protection* with this explicit qualification.

## Phase 1 — Product Owner

**Objective:** Rename the AI **Interviewer** role to **Product Owner** in UI, prompts and display while preserving saved records and API compatibility.

The role is a pragmatic experienced product leader: clarify users, goals, workflows, functional and nonfunctional needs, constraints, assumptions, contradictions, must-haves, optional work and testable acceptance criteria. Continue adaptive one-or-two-question conversation; never auto-approve or execute.

**Gate:** UI terminology consistently uses Product Owner; prompt identity/behavior improved; existing interview history readable; new interviews meaningfully better; specification and generator regression tests pass. Provide both deterministic evidence and explicitly labelled live-model examples.

## Phase 2 — Technical Stack and Architecture Discovery

**Objective:** Elicit proportionate choices for languages/frameworks, backend/frontend/CLI/desktop scope, storage, target OS/deployment, privacy/auth, tests, operations and performance. Recommend justified architecture alternatives rather than automatically choosing complexity.

Examples include layered, Clean, Vertical Slice, modular monolith, hexagonal, CQRS and event-driven patterns where appropriate. These are composable, not artificially exclusive. Capture **versioned structured decisions** with status (selected/approved/recommended/deferred/not applicable/unresolved blocking), alternatives, rationale and tradeoffs. Enforce context-sensitive specification readiness without a rigid 30-question questionnaire.

**Gate:** Chosen stack and rationale persist; user-approved decisions differ from model suggestions; genuinely blocking decisions are visible; spec includes approved technology/architecture; reasonable alternative and tradeoff explanations; old generation flow and tests still work.

## Phase 3 — Microsoft Agent Framework Foundation

**Objective:** Add **supported, currently verified Microsoft Agent Framework (MAF)** APIs in existing .NET 10 backend; connect through a local `IChatClient`/Ollama adapter. Do not substitute a bespoke orchestrator or require Azure.

Migrate **Product Owner first**, with a controlled compatibility seam. Preserve authenticated API, SQLite, interview history, prompt semantics, owner approvals, durable events and execution isolation. MAF coordinates agents; it does **not** grant the Product Owner shell/file/process privileges.

**Gate:** Verified MAF packages/API integration; local Ollama inference by MAF-backed Product Owner demonstrated; no cloud provider required; current persisted interviews/specifications and external API contract remain compatible; regression and live-model tests pass; no new executable authority.

## Phase 4 — Distinct Engineering Team

**Objective:** Six bounded professional identities with a **deterministic, sequential** workflow:

Human objective → **Product Owner** (requirements) → **Solution Architect** (technical options) → **Human approval** → **Technical Lead** (bounded plan) → **Software Engineer** (source) → **QA Engineer** (test plan/results) → **Security Reviewer** (assessment) → Human review and deliverables.

Agents must exchange explicit, versioned contracts and immutable enough history to audit outputs, model/version, decisions and approvals. The Software Engineer may only propose/run work within approved scope, and actual executable tool access remains behind the existing reviewed boundary. QA/Security cannot claim independent execution on model opinion alone.

**Gate:** Six roles with different responsibilities *and demonstrated collaboration*, traceable artifacts, enforced approvals, durable progress and failure/recovery behavior, scoped operations and representative local-model plus model-free acceptance. Six prompts/classes alone are insufficient.

## Phase 5 — Mission Control Integration

**Objective:** One React UI for entering arbitrary objectives, talking to Product Owner, inspecting/approving stack and architecture choices, reviewing specifications and task plans, observing agents, inspecting source/tests/security evidence and downloading artifacts.

Read progress from real durable backend events, with distinct states for inference, output, approval wait, scoped execution, verification, completion, interruption and failure. Show honest elapsed time, streaming when available, limited retries, cancellation, history and reconnection. No fabricated completion percentages or hard-coded demo tasks.

**Gate:** End-to-end owner workflow from fresh arbitrary objective to reviewed and downloadable output; six-agent progression and decisions visible; exact approval gates work; verification and unverified work clearly separated; regression tests and live-model acceptance pass on the appropriately qualified host.

## Existing blocking vs nonblocking work

- **Release/security blockers:** [#76](https://github.com/dvdhtzr/CodingAssistant/issues/76) integrated execution admission and adversarial checks; [#92](https://github.com/dvdhtzr/CodingAssistant/issues/92) genuine task-scoped agent work. Neither may be silently bypassed as MAF is integrated.
- **Debt and future capabilities:** CPU-only inference speed, streaming/metrics, npm lock/audit [#82](https://github.com/dvdhtzr/CodingAssistant/issues/82), Windows-host isolated runtime qualification, broader language support and skills. Schedule when relevant; do not let nonblocking debt displace the six phases.

## Phase reporting

For each phase report **implemented changes, concrete test/evidence, PR/merge/main SHA, remaining limitations and next phase**. Status is **GREEN** (all criteria evidenced), **YELLOW** (incomplete), **RED** (failed checks) or **BLOCKED** (specific prerequisite missing). Never mark GREEN merely because code merged or a model reported success.
