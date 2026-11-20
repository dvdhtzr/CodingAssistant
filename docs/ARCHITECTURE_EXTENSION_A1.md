# Architecture Extension A1 — Portable Skills, Runtime Profiles and Verified Artifacts

**Decision:** APPROVED by the human owner, November 20, 2026.  
**Implementation status:** DESIGN ONLY. No new executable privilege, skills installation, containers, or verification capability is enabled by this document.  
**Authority:** Extends [Development Plan v2.0](DEVELOPMENT_PLAN_V2.md), not a replacement for its six sequential phases. Historical [M0–M3 implementation](IMPLEMENTATION_PLAN.md) and [security requirements](SECURITY.md) remain binding.

## Why we are making this change

The owner exercised CodingAssistant's real local-Ollama Product Owner → approved specification → source-generation → ZIP path with an offline calculator ("Calculation Journal"). The generated UI rendered, but had no working mouse or keyboard controls. The source ZIP had an HTML placeholder script and did not link the generated JavaScript; that JavaScript also targeted non-existent HTML selectors ([regression #107](https://github.com/dvdhtzr/CodingAssistant/issues/107)). The existing generator successfully saved **source text**, not a functioning, verified application.

At the same time, the executable project pilot is intentionally narrow: [`TaskBuildStore.Prepare`](../src/CodingAssistant.Api/TaskBuildStore.cs) requires a `.csproj` and only `.cs`/`.csproj` files, while [`LocalProjectDraftModel`](../src/CodingAssistant.Api/LocalProjectDraftModel.cs) plans at most five source files. [`TaskAgentContract.Preview`](../src/CodingAssistant.Api/TaskAgentContract.cs) uses a separately qualified pinned model. The [task-project coding pilot](M1_TASK_PROJECT_PILOT.md) currently qualifies a limited Linux/.NET scope. These restrictions are **security and MVP limits**, not a product goal. Do not delete them until replacement runtime profiles have been independently qualified.

**Product target:** A local-first, language-agnostic engineering system that can interview, decide a stack, design, implement, independently build/test, perform bounded evidence-driven repair, and produce an inspectable artifact with truthful verification status.

## Approved architectural decisions

1. **Portable Agent Skills:** Use the [Agent Skills specification](https://agentskills.io/specification) (`SKILL.md` + optional references/resources). Skill instructions can be provided to local models; no hosted model is required. Prefer native [Microsoft Agent Framework skills integration](https://learn.microsoft.com/en-us/agent-framework/agents/skills) after verifying the exact supported .NET packages/APIs in Phase 3.
2. **Skills Registry:** Before a skill is activated, retain its source URL/repository, immutable commit or release, relevant file hashes, license, declared compatibility, reviewed purpose, version, required inputs, and capability classification. Skills from Anthropic/Microsoft/others are third-party code and instructions: **not trusted by origin alone**. Load approved reference/instruction text first; scripts and tool suggestions cannot run with authority they have not separately been granted.
3. **Runtime Profile Registry:** Replace the executable path's implicit `.csproj` rule with explicitly **versioned, qualified** profiles: supported languages/file types, immutable toolchain/evaluator image digests, dependency policy, compile/build/test strategy, resource ceilings, environment restrictions, supported host platforms, and minimum verification criteria. A proposed but unqualified profile must remain non-executable.
4. **Container lifecycle:** Reuse trusted, pinned images; create a **new disposable, per-attempt isolated guest/workspace**, not a freshly built image per project. A project can have durable source/evidence but must not share a running untrusted guest across unrelated attempts. Isolation is enforced by the host, not by the model's instructions.
5. **Platforms:** Rootless Podman on qualified Linux hosts first. Docker Desktop/WSL2 for Windows is a **future, separately qualified** adapter. Do not equate ordinary Docker on Windows with the existing Dell isolation evidence. No Docker/Podman socket or credentials inside any untrusted guest.
6. **Network/dependencies:** No external network by default during generation/build/test. Dependency acquisition, if required, is a **separate trusted, reviewed, offline-cache-populating operation** with explicit owner permission, license/security inspection, exact lockfiles/hashes, and provenance. Do not give model-generated install scripts an open network route.
7. **Independent verification:** The coding agent cannot mark its own result `VERIFIED`. A separate, toolchain-pinned host evaluator determines build, test count/outcomes, browser interaction results where applicable, and security checks. Exit status, stderr excerpts, test logs and evidence digests are bounded, private and auditable.
8. **Bounded repairs:** Feed structured compiler/test diagnostics to the Software Engineer for at most two repair proposals/attempts initially, subject to the original project/file scope and approved runtime. Every attempt gets an independent re-evaluation. No unbounded self-repair or automatic repo modification.
9. **Artifact truth:** Permit download of safely packaged draft source even when verification fails; label it `GENERATED_NOT_TESTED`, `BUILD_FAILED`, `TESTS_FAILED`, `VERIFICATION_BLOCKED`, or `VERIFIED` with evidence. A build with zero relevant tests does not qualify as fully verified. Never silently call a source ZIP a released working app.
10. **Human authority:** Approval of specification/architecture decisions is separate from approval of exact execution scope. Execution contract binds the selected runtime profile+digest, source/plan hash, skill identities+hashes, model+digest, image+evaluator digests, permitted paths, environment/resource/network policy, and acceptance tests. Any meaningful change invalidates execution approval.
11. **Existing architecture preserved:** .NET 10 backend, React/TS Mission Control, SQLite, Ollama local inference, Microsoft Agent Framework for orchestration, and OpenClaw/ACPX/OpenCode execution boundary remain. No direct tools, shell or host filesystem privileges are given to Product Owner, Architect, or Security Reviewer.
12. **Incremental scope:** Qualify a **Static Web** profile first (HTML/CSS/JS with bounded Node syntax tests and independently run headless Chromium browser acceptance); then React/TypeScript, .NET/C#, and Python. Future Java/Go/Rust are candidates, not promised or accepted implementations.

## Proposed components and contracts (design targets; not existing APIs)

- **`ArchitectureDecision`**: task, specification version/hash, topic, options, tradeoffs, recommended value, selected value, `proposed|approved|deferred|not-applicable|blocking`, decision owner and timestamp. Recommendations must not masquerade as approved choices.
- **`SkillDescriptor` / `SkillActivation`**: identity, version, source, immutable digest, license, authorized roles, instruction-only vs resource/script requirements, bound task, activation timestamp and exact loaded content hash. A skill is *context*, not a trusted execution principal.
- **`RuntimeProfile`**: ID/version, host platform, allowed source extensions, runtime-image digest, evaluator-image digest, allowed deterministic commands/entry points, dependency cache policy, resource limits, network requirements, required tests, qualification status and evidence.
- **`ExecutionContract`**: immutable task/spec/source hash, chosen profile and images, selected skills and their hashes, exact permitted paths/mounts, model digest, test plan, permissions, risk acknowledgements and separately approved executable hash.
- **`VerificationReport`**: build results, test names/count/exit codes, browser interactions, security outcomes, immutable evidence references, per-attempt diagnostics and final status. Never synthesize evidence from model prose.

Any serialization schema is to be explicitly versioned and exhaustively tested before production persistence. These are **draft contract names**, not permission to change current SQLite/HTTP APIs immediately.

## Execution and verification boundary

1. Product Owner obtains scope and acceptance criteria; Solution Architect recommends a stack and matching *available* runtime profile.
2. Human approves/rejects actual architectural choices and the versioned product specification.
3. Technical Lead decomposes work. Software Engineer generates source into a disposable task directory; instructions/skills are selected from an allowlisted registry. Source remains untrusted.
4. Host statically validates file paths, links, imports, placeholder-only files, manifest, size, and task scope; failures become truthful diagnostics.
5. Host prepares immutable execution contract and **requires separate exact-scope owner approval**. A `PENDING_PROFILE_QUALIFICATION` profile cannot be executed.
6. Trusted host creates a fresh rootless disposable container using a previously reviewed immutable toolchain image. No host home/secrets, no writable original repo, no host networking/daemon socket, no capabilities, limited CPU/RAM/PIDs/time, and carefully scoped mounts. The coding agent and independent verifier are segregated.
7. The independent evaluator runs the profile's finite allowlisted build/test operations with no inference socket or external network. Save actual results and hashes.
8. If failing, the coordinator may request up to two scoped repairs; reauthorize any scope change; independently rerun every check. No verification claim if tests did not run.
9. Package validated source plus a human-readable, machine-readable verification report. Show an honest result and artifact status; no automatic application to a real repository.

**Qualification is profile+host specific.** GitHub model-free CI confirms contract logic but does not constitute adversarial live sandbox qualification. Follow [#76](https://github.com/dvdhtzr/CodingAssistant/issues/76) and [#92](https://github.com/dvdhtzr/CodingAssistant/issues/92), especially integrated ten-case negative security tests and Windows-specific qualification. Reject dangerous mounts, symlink/hardlink escapes, child-process/network/Git escapes, container cleanup failures and failed admission.

## Initial profile: Static Web / Calculation Journal

- Artifacts: HTML, CSS, JS and bounded project metadata/test assets, rather than a fixed five-file output. Do not enable unlimited file generation: replace the old cap with profile-aware path/file/byte budgets.
- First gate is *static*: all `<script src>`, `<link href>`, imports and relevant DOM selectors resolve; placeholder-only handlers are rejected; JavaScript parses. No model-reported success counts as verification.
- Second gate is *isolated real browser behavior*: in a qualified guest, execute mouse, keyboard, arithmetic, backspace, divide-by-zero, Back/history, and persistence after reload/browser reopen where supported.
- One canonical frozen acceptance fixture reproduces [#107](https://github.com/dvdhtzr/CodingAssistant/issues/107). The **original failing archive** is the negative test. The separately manually repaired calculator is a reference example but **not evidence of CodingAssistant autonomously repairing it**.
- A run is `VERIFIED` only when independent static, functional and security checks actually pass with nonzero relevant tests. A failed test produces a helpful, downloadable diagnostic report and a source-only ZIP marked unverified.

## Integration with approved six-phase roadmap

| Phase | Extension-specific deliverable and gate |
|---|---|
| **1 — Product Owner** | Finish documented local-Ollama conversation/specification quality acceptance. Prior source generation alone does not prove the conversation meets all Phase 1 criteria. No Phase 2 GREEN claim. |
| **2 — Stack/Architecture Discovery** | Versioned, approved architecture decisions; `RuntimeProfile` *descriptors* and capability/qualification reporting; exact proposal/approval separation. No executable runtime permissions. |
| **3 — MAF foundation** | Integrate supported local MAF skills and a read-only/allowlisted `SKILL.md` registry. Confirm actual .NET package/API versions and no silent cloud calls. Executable skill scripts remain disabled until independently reviewed. |
| **4 — Engineering Team** | Bounded sequential agent workflow and actual independently qualified isolated build/test/repair host. Qualify Static Web first. Integrate immutable contracts, owner admission and [#76](https://github.com/dvdhtzr/CodingAssistant/issues/76) negative cases; then add other profiles one at a time. |
| **5 — Mission Control** | Show approved stack/profile/skills, exact scope review, real progress, test evidence, repairs, and honest artifact statuses. Project ZIP always differentiated from independently verified delivery. |

**Non-goals now:** unrestricted agent shell/host access, cloud model fallback, arbitrary public skills installation/execution, autonomous dependency downloads, one fresh image build per user project, unsigned toolchain images, or announcing a generalized working coding agent on the basis of a generated ZIP.

## First actionable PR sequence

1. Record this architecture amendment and backlog; preserve Phase 1 evidence/status.
2. **Phase 2A (first implementation once Phase 1 gate closes):** add a read-only runtime-profile descriptor registry and tests, seeded with `static-web` **UNQUALIFIED** and legacy pinned `dotnet-pilot` status. Verify profile selection never starts a container or changes existing fixed scope.
3. **Phase 2B:** add versioned architecture-decision records and owner-approval endpoints/contract tests without replacing saved sessions.
4. **Phase 3:** integrate native MAF and skills; begin with vetted instruction-only first-party `SKILL.md` examples; validate third-party licenses and repository commits before activation.
5. **Phase 4A:** implement and qualify Static Web container/Chromium independent evaluator on Linux; only after adversarial security gates connect execution and bounded repair.
6. **Phase 4B / Phase 5:** integrate UI/verified report and add React, .NET and Python profiles in independently reviewed increments.

## External references (reference only, not trusted executable inputs)

- [Agent Skills specification](https://agentskills.io/specification) and [reference repository](https://github.com/agentskills/agentskills).
- [Anthropic skills examples](https://github.com/anthropics/skills) — license and content review per skill.
- [Microsoft Agent Framework Agent Skills](https://learn.microsoft.com/en-us/agent-framework/agents/skills), [sample implementation](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/02-agents/AgentSkills).
- [Docker rootless mode](https://docs.docker.com/engine/security/rootless/) and [bind-mount security implications](https://docs.docker.com/engine/storage/bind-mounts/).

**Evidence standard:** This architecture has owner approval. Its runtime features, security and functional acceptance have **not** been implemented by approving this document.
