# Code Atlas — Implementation Baseline (2026-11-22)

**Status:** Owner approved implementation on an isolated branch; no merge into `main` without explicit final approval.
**Baseline:** `8593f8fe74ebbb2611adf91949260b184113f7c6` on `main`.
**Feature branch:** `feature/code-atlas-20261122`.
**Scope:** Code Atlas (existing repositories) and Code Atlas Live (approved Software Engineer source generation).
**Relationship:** Additive capability beside the binding `DEVELOPMENT_PLAN_V2.md`, `SECURITY.md`, `IMPLEMENTATION_PLAN.md`, and `ARCHITECTURE_EXTENSION_A1.md`. This document **does not** authorize execution of untrusted code or relax product plan/hash approvals.

## Product contract

Code Atlas renders a trustworthy, searchable, progressively explorable graph of source entities and known relationships. Code Atlas Live shows those entities appearing and changing as **accepted source is actually persisted**, never on the strength of agent narration.

- Source may be **generated-but-unverified**; graph membership does not imply compilation, testing, or execution.
- All graph entities and relationships have traceable source identity and explicit analysis coverage.
- Show both **Follow Build** (focus on newly materialized source) and **Explore Freely** (preserve the user's viewpoint).
- Graph explorer shares the existing Mission Control right-side drawer patterns, source artifact explorer, and agent/QA context; no competing orchestration or permission system.
- Existing repositories are pinned to a commit; generated projects are bound to task/run/source revision.

## M0 audit findings (verified on the baseline commit)

- API: .NET `net10.0`, `Microsoft.Agents.AI 1.28.0`, `Microsoft.Data.Sqlite 10.0.14`.
- Frontend: React 19.3.1, TypeScript 5.9.3, Vite 7.3.8, `@microsoft/signalr 10.0.11`; no graph visualization library present in `package.json`.
- `EngineeringRunCoordinator` calls `IEngineeringTaskModel.ImplementAsync` once per **approved** Technical Lead work item and persists accepted complete replacements with `EngineeringRunStore.CompleteTask`.
- `EngineeringRunStore` saves accepted source text and hashes under `engineering_run_files`; work-item activity is durable in `engineering_run_events`. This is the initial authoritative Code Atlas Live input.
- `EngineeringWorkspace.useEngineeringRun` currently polls the saved engineering snapshot every three seconds. `SourceArtifactExplorer` already supplies source navigation, file paths, contents, and line display.
- `RunProgressHub` currently publishes its own run events. Do **not** assume Software Engineer engineering-run events already stream through that hub. Reuse established secure transport patterns, not unrelated event identities.
- Engineering runs currently persist at most 12 distinct source files. Source generation is **not** automatically a filesystem workspace or executable project.
- Existing source generation can produce static-web/TypeScript, so graph V1 **must** show universal files and containing structure even when semantic indexing is only implemented for C#.
- Windows Docker/Chromium and arbitrary source execution remain separately gated. Atlas source indexing must run without those capabilities.

## Canonical graph model

A `GraphSnapshot` is identified by a project/run or imported repository, an exact source revision, and monotonic graph version. Each `GraphNode` has a stable within-revision ID, kind, label, parent, source location, evidence status, and optional language-specific symbol identity. Each directed `GraphEdge` has a type, endpoints, supporting source location(s), and resolution status.

Required initial node kinds: repository, solution, project, folder, namespace, source file, class, interface, struct, enum, method, constructor.
Required relationships as support becomes available: contains, project-references, inherits, implements, references, calls, constructor-depends-on.
Never present an unresolved static call as a confirmed call. Support deletion, rename, duplicate-looking identifiers, partial declarations, overloads, and source revisions explicitly.

A `GraphDelta` contains base/new version, sequence number, additions, changes and removals. Consumers deduplicate, detect gaps, and reload an authoritative snapshot. Events and graph state are connected to persisted source **after** commit. Failure to index must not fail or roll back accepted engineering source; reconciliation repairs stale graph state.

## Phase plan and acceptance gates

### M1 — Foundation and source-grounded graph
- Implement a language-neutral graph contract and bounded index/query API.
- Index folder/file hierarchy for every supported generated-source stack.
- Add Roslyn-based C# syntax indexing without running MSBuild or untrusted build targets. Semantic enrichment is separate and must honor the execution boundary.
- Test deterministic IDs, duplicates, generic/nested/partial symbols, invalid C#, untrusted text, safe paths, and correct source lines.
- **Gate:** A saved multi-file engineering run produces a reproducible source-grounded graph and explicitly reports unsupported/incomplete coverage.

### M2 — Interactive Code Atlas
- Embed a progressive graph in Mission Control with project/namespace/type views, search, pan/zoom, focus, edge filtering, and source drawer navigation.
- Start with small bounded neighborhoods, not a whole-repository hairball. Preserve keyboard/reduced-motion accessibility and a relationship list.
- Choose visualization technology only after verifying repo UI needs; new dependencies must remain isolated to the feature.
- **Gate:** An actual indexed run can be explored without reading raw JSON; frontend tests exercise primary controls.

### M3 — Code Atlas Live
- After each persisted `EngineeringRunStore.CompleteTask`, publish or reconcile a new source revision and graph snapshot/delta. Do not write to or execute generated source.
- Show additions/updates/removals; keep positions stable; support Follow Build / Explore Freely.
- Persist sufficiently for refresh/reconnect; never imply model token streaming is source materialization.
- **Gate:** A controlled run adding and replacing files across multiple assignments updates the graph faithfully, including after reconnect/restart.

### M4 — Semantic Code Intelligence
- Resolve C# method calls, inheritances, interface implementations, constructor injection, and project references using Roslyn semantic data when safely available.
- Label resolution and confidence; add bounded caller/callee exploration and source evidence.
- **Gate:** Independently authored fixtures validate the exact relationships; unresolved dynamic/reflection paths are not misrepresented.

### M5 — Build Replay
- Record milestone graph versions, checkpoint snapshots, and durable deltas with bounded retention/compaction.
- **Gate:** Reconstruct history deterministically after restart and return to current state.

### M6 — Agent / QA overlays
- Add read-only graph query tools for existing agents and link current QA findings, repair proposals, test/build evidence to source nodes.
- Keep Product Owner/Architect/Engineer/QA approval and independent verification boundaries unchanged.
- **Gate:** A known QA finding can be traced to affected source and independently verified repair evidence.

### M7 — Imported repository and qualified runtime integration
- Allow commit-pinned GitHub imports, safe read-only indexing and language coverage reporting.
- Runtime diagnostics only when the separate, host-qualified sandbox policy and explicit execution authorization permit them.
- **Gate:** Read-only import is useful with no Docker; runtime evidence is accurately attributed or reported blocked.

### M8 — Future expansion
- TypeScript/JavaScript, Python/Java semantic indexers, cross-language edges, historical architecture drift, large-graph optimization, and opt-in runtime traces.

## Quality and safety gates

- Unit tests: graph identity, parentage, relationship direction, deterministic output, diff/reconcile, malformed/incomplete source, missing references.
- API tests: scope isolation, auth, pagination/limits, source revision binding, gap recovery, cancellation.
- UI tests: search/focus/expand, correct node details, stable viewport during updates, reduced motion, keyboard accessibility, honest verification labels.
- E2E tests: deterministic task fixture adding model, interface, service, endpoint, tests; graph matches independently inspected persisted source; build/QA overlay never reports unrun tests passed.
- Security tests: symlink/path escape, malicious source text, huge files, unsupported project, no network/process invocation from source indexing.
- Avoid guessed perf promises; measure cold scan and incremental update latency on representative fixtures, set budgets after baseline.
- Do not mark a gate GREEN without reproducible evidence. In unavailable test environments report NOT_RUN/BLOCKED and keep the gate open.

## Authorized execution protocol

1. Inspect newest `main` and any concurrently changing integration surfaces; maintain isolated branch/worktree.
2. Work in bounded milestones, build/test when a qualified toolchain is available, repair within scope, record evidence and technical debt.
3. Advance independently through gates that have actual evidence; otherwise explain exact blocker.
4. Seek user input only for substantive scope/security/permission changes or final merge.
5. No merge to `main` without an explicit request and approval. No background execution claim.

## M0 known risks and resolution

1. **Current generator saves source in SQLite, not necessarily physical files.** Use persisted source as the initial feed; add a workspace-observer adapter only when actual approved filesystem-backed runs exist.
2. **Generated projects may be HTML/JS rather than C#.** Present file-level graph universally before richer C# semantics.
3. **EngineeringRunStore has a 12-file cap.** Keep bounded graph payload and warn about partial source; do not change generator limits under this feature.
4. **Event streams are not interchangeable.** Use engineering-run identity and durable source hashes; avoid attaching graph data to unrelated RunStore event sequences.
5. **This development environment may not run .NET.** Never claim local compilation/acceptance without evidence. Use existing CI and owner-host verification where needed; do not weaken verification criteria.

## M8.1 — Historical architecture drift (incremental expansion)

- A read-only owner-authenticated `GET /api/tasks/{id}/engineering-runs/{runId}/atlas/drift?from=N&to=M` compares two already-committed, run-scoped Build Replay checkpoints.
- Reports added/removed projects, namespaces, types, methods, and confirmed static relationship edges. Structural membership alone is not build, test, or runtime verification.
- Show the comparison on a selected historical checkpoint against its preceding saved checkpoint; do not substitute current source text or current QA evidence.
- Output is deterministic, bounded to 100 detailed changes with full aggregate counts, and rejects cross-run, invalid-order, and revision-mismatched comparisons.
- This is the first bounded M8 slice. TypeScript/JavaScript/Python/Java semantic indexing, cross-language edges, further large-graph optimization, and qualified runtime traces remain future work.
