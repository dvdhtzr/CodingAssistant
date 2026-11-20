# Phase 2 foundation — Runtime profiles and architecture decisions

**Implementation:** Issue [#109](https://github.com/dvdhtzr/CodingAssistant/issues/109) — first planning-only slice of [Architecture Extension A1](ARCHITECTURE_EXTENSION_A1.md).

**Security posture:** This API and database schema DO NOT grant execution permission. The owner attested Phase 1 acceptance on November 20 and issue [#108](https://github.com/dvdhtzr/CodingAssistant/issues/108) was closed with an explicit note that the exact model and transcript were not retained. Phase 2 remains incomplete until the real local Solution Architect review is accepted.

## Runtime profile registry (schema v1)

- Authenticated \`GET /api/runtime-profiles\`: returns a versioned, read-only list, plus \`registryExecutionAvailable: false\`.
- Authenticated \`GET /api/runtime-profiles/{id}\`: returns one declared profile; unknown IDs return 404.
- \`static-web\` version \`0.1.0\`: **UNQUALIFIED**, no executable entry points, images or resource ceilings yet. These are deliberately unset until independent Linux/Windows host qualification, including a real Chromium evaluator.
- \`legacy-dotnet-pilot\` version \`0.1.0\`: **LEGACY_SCOPED_PILOT_ONLY**. It describes the pre-existing, narrowly qualified M1 C#/.csproj pilot; it cannot be selected to enable general .NET, nor can any execution be requested through this registry.
- No runtime profile mutation, image download, guest/container creation, dependency acquisition, skills loader or model inference is added.

The capabilities here are **descriptions**, not a declaration that arbitrary source has passed tests. Profile qualification, immutable image digests, approved resource limits and independently measured evidence must arrive before executable admission in Phase 4. Windows Docker Desktop/WSL2 needs its own host security qualification.

## Architecture decisions (schema v1)

A proposal is a task-scoped, versioned set of **2–6 options** (stable option ID, label, benefit, tradeoff and optional known runtime-profile ID), a recommended option, and a rationale. It is bound to the **currently approved** Product Owner specification version and SHA-256. It is not approved merely because the model recommended it.

Authenticated endpoints:

| Method | Path | Meaning |
|---|---|---|
| GET | \`/api/tasks/{id}/architecture-decisions\` | Latest revision of each topic, with effective specification freshness |
| GET | \`/api/tasks/{id}/architecture-decisions/{topic}/history\` | All retained revisions in order |
| POST | \`/api/tasks/{id}/architecture-decisions\` | Propose next revision with optimistic \`expectedRevision\` |
| POST | \`/api/tasks/{id}/architecture-decisions/{topic}/review\` | Human approval of a **selected** option, or explicit deferral |

A proposal body uses \`schemaVersion: 1\`, \`specificationVersion\`, \`specificationSha256\`, \`topic\` (e.g. \`technology-stack\`), \`recommendedOptionId\`, \`rationale\`, \`options\` and \`expectedRevision\` (0 for a new topic). Only registered profile IDs may appear in option descriptors. The server computes its own canonical proposal hash and saves a revision to private SQLite in a transaction.

Review uses \`schemaVersion: 1\`, \`expectedRevision\`, \`proposalSha256\`, \`action: "Approve" | "Defer"\`, \`selectedOptionId\` (required for approval, null for deferral) and exact \`confirmation\` of \`APPROVE <proposal-sha256>\` or \`DEFER <proposal-sha256>\`. A recommendation is never auto-selected. A changed proposal, stale revision, bad hash, changed or unapproved specification, non-listed selection or replay is rejected. Later proposals supersede earlier decisions without deleting historical revisions. A Product Owner specification change marks every old decision \`effectiveStatus: "STALE_SPECIFICATION"\` on read, even when its historical record was Approved.

**Not implemented in this slice:** agent-generated recommendations, a Solution Architect conversation/UI, auto-population from interviews, enforcement that all blocking decisions have been answered, executable runtime selection, and a full Phase 2 readiness gate. They must follow the [Development Plan v2.0](DEVELOPMENT_PLAN_V2.md) and issue #109 without altering old interview, execution or ZIP endpoints.

## Deterministic test coverage

Cross-platform GitHub Actions runs:

\`\`\`sh
dotnet run --configuration Release --project tests/CodingAssistant.Phase2.Smoke/CodingAssistant.Phase2.Smoke.csproj
\`\`\`

This runs a model-free, real HTTP + private SQLite exercise: token enforcement; profile qualification truth; no profile mutation; unknown profile/schema rejection; approved spec/version binding; recommendation-versus-human-selection separation; exact hash confirmation; stale/concurrent revision handling; supersession history; old interview persistence; restart durability; specification invalidation; deferral; and absence of a generic execution endpoint. It neither contacts Ollama nor runs generated code or a container.

CI success proves only those deterministic contracts on its two hosted operating systems. It does **not** satisfy live Product Owner quality acceptance [#108](https://github.com/dvdhtzr/CodingAssistant/issues/108), local container qualification [#76](https://github.com/dvdhtzr/CodingAssistant/issues/76), or working Calculation Journal browser QA [#111](https://github.com/dvdhtzr/CodingAssistant/issues/111).


## Second slice — Local Solution Architect recommendations and owner review

- Authenticated \`POST /api/tasks/{id}/architecture-decisions/recommend\` takes
  \`schemaVersion:1\`, \`specificationVersion\`, \`specificationSha256\`,
  \`topic\` (\`technology-stack\` or \`architecture-style\`) and
  \`expectedRevision\`.
- It validates the **currently approved Product Owner specification**,
  selects that specification's **exact local model tag**, verifies the tag is
  still installed via the Ollama catalog, and asks the planning-only Solution
  Architect for 2–4 alternatives with specific benefits/tradeoffs.
- The Ollama transport is fixed to loopback with no proxy, redirects, tools,
  shell access or cloud fallback. The response must be bounded JSON; the
  existing \`ArchitectureDecisionStore.Propose\` validates every option,
  runtime-profile reference and expected revision before persisting.
- Mission Control now shows both decision topics, clear qualification
  limitations, the model's recommendation **without preselecting it**, explicit
  radio selection, and full-hash confirmation for approval or deferral. Saved
  decisions reload after restart; stale specifications invalidate effective
  decisions, and old proposals cannot silently regain authority.
- Model-free cross-platform API tests use an injected advisor and local-model
  catalog to check that no network inference is required for deterministic
  checks. A live owner-Ollama acceptance is separately required to assert
  recommendation *quality* or evaluate actual model output.

**Current limits:** no actual MAF integration yet (Phase 3), no runtime
execution or profile qualification, and the existing source-only draft generator
does **not** consume approved architecture decisions yet. The next Phase 2 slice
must bind the chosen stack/architecture to source planning without bypassing
unqualified language/runtime security and provide end-to-end owner acceptance.


## Third slice — Architecture-bound source-only draft handoff

Owner-approved architecture selections now influence **both** project-file planning
and per-file source writing. This is a planning/source-text change only, not
build verification or a new executable runtime.

### Immutable binding and admission

Authenticated GET \`/api/tasks/{id}/draft-build/architecture\` reports
\`status\`, \`readyForSourceGeneration\`, \`architectureSha256\`,
\`blockingTopics\` and \`approvedChoices\`. New Mission Control draft generation
is disabled until both \`technology-stack\` and \`architecture-style\`
have **current approved selections** for the exact approved specification.

Authenticated POST \`/api/tasks/{id}/draft-build\` now supports an optional
\`architectureSha256\` request field. For a project with approved decisions,
this hash **must** match the canonical, owner-selected architecture captured
within the SQLite job-admission transaction. A missing or changed hash
is rejected. If any decision has been proposed but the two-topic review is
incomplete, generation is rejected with \`ARCHITECTURE_NOT_READY\`.
A changed decision after admission yields \`ARCHITECTURE_CONFLICT\`.

New drafts store an immutable contract per draft: specification version/hash,
the two selected topic option IDs, labels, tradeoffs, runtime-profile
references, proposal versions/hashes and a canonical architecture SHA-256.
The same immutable values are appended to the planning and source-writing
model context. The job produces a durable \`ArchitectureBound\` event and its
stored binding is visible in the source-draft read API. During each file save
and final successful transition, the backend revalidates the approved
specification **and selected architecture in the same transaction**.
A changed selection causes failure, never a false successful draft.

The \`static-web\` profile is still **UNQUALIFIED**. Its owner selection now
constrains source-only plans to include \`index.html\` and only
\`.html\`, \`.css\`, \`.js\`, \`.json\`, \`.md\` text file types.
An incompatible model-generated plan fails with
\`ARCHITECTURE_PLAN_CONFLICT\`; it does not fall back to C#, silently
replace the approved stack, run JavaScript, install packages or run browsers.
This guard does not prove the generated source functions correctly; that
belongs to Phase 4 independent verification.

### Compatibility

Historical draft jobs and ZIP files remain readable; a missing stored
architecture binding clearly indicates a legacy specification-only draft.
For **old API consumers with no architecture-decision rows at all** the
spec-only source generation path remains available for backward compatibility,
and its first durable event explicitly says \`Legacy spec-only\`.
**Mission Control does not silently use this fallback**: it blocks new jobs
until both decisions have been reviewed. Once a task has any architectural
proposal, partial, deferred, superseded or stale decisions block API admission
as well. No existing specification/decision approval becomes permission to
execute generated code.

Cross-platform model-free regression tests cover readiness, zero/partial/two
decisions, exact fingerprint admission, the bound model plan/write prompts,
persisted architecture hash/event, compatibility with existing source-only
HTTP/SQLite flows, static-profile path rejection and decision-change races.

**Still not verified:** actual Ollama compliance with nuanced architectural
styles, a linked application passing build/browser tests, arbitrary language
runtime profiles, MAF agent handoffs, skills activation and adversarial
execution qualification. Those retain their roadmap gates.
