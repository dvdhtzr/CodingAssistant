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
