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

## M8.2 — TypeScript/JavaScript structural visibility

- Read-only, bounded managed-code tokenizer adds source-backed TypeScript/TSX/JavaScript/JSX/MJS/CJS top-level named classes, interfaces, functions, top-level arrow bindings, and class methods/constructors to the existing Code Atlas graph.
- Works for accepted generated source and imported commit-pinned files using existing authentication/project identity and saved-source revisions. No Node, package installation, MSBuild, shell, network fetches, or untrusted-source execution during indexing.
- Pure declaration structure only: no TypeScript type checking, call resolution, inferred inheritance, imported-package semantics, JSX component verification, or runtime/test claims. Coverage is explicitly `ECMA_STRUCTURE_ONLY`, `PARTIAL_ECMA_STRUCTURE`, or mixed C#/JS/TS coverage.
- Comments, quoted strings, templates and regex literals are not treated as declarations. Template expressions and malformed/unbalanced source are diagnosed as partial coverage. Node/edge and per-file token caps stay enforced.
- This is an incremental M8 capability, not a full ECMAScript parser. Full compiler-backed semantic resolution and more language support remain open.

## M8.3 — Compare saved GitHub commits

- Compare two already-imported, immutable GitHub commit snapshots from the same repository and task. No new network fetches, package restores, untrusted source execution, or implied commit ancestry.
- Normalize imported graph root IDs to stable source-backed identities so random import IDs do not create false changes. Includes C# semantic edges, JS/TS structural functions, and source file additions/removals, with explicit coverage.
- The import panel offers a compact starting-commit selector and a bounded comparison view. The independent agent approval, owner authentication, revision binding, and project isolation contracts remain unchanged.

## M8.4 — Literal local JavaScript/TypeScript module links

- In addition to source-backed JS/TS declarations, Code Atlas now records `ModuleImports` **file-to-file** links for top-level static ESM `import`, side-effect `import`, and `export ... from` when the literal relative specifier resolves **uniquely to a saved file**.
- Resolution is purely lexical and in-memory: no filesystem access, bundler, TypeScript compiler, package installation, network call, build, or untrusted code execution. Supported exact paths, unambiguous extensionless `.ts/.tsx/.js/.jsx/.mjs/.cjs`, and `index.*` are source-backed. Package names, alias resolution, runtime `import()`, `require()`, and inferred symbol calls are omitted.
- The relationship carries `LiteralLocalModule` resolution plus import source path and line. Ambiguous, missing, and escaping local specifiers produce bounded diagnostics without guessed edges. Imports from comments, templates, strings, regex, and dynamic expressions do not create edges.
- Existing task/import isolation, owner authentication, Node/edge caps, diff/replay snapshots, and independent test/approval boundaries remain intact.

## M8.5 — Bounded large-graph symbol jumps

- Search now surfaces up to eight real, ranked matches across the entire saved/imported source graph rather than relying on the first matching node or the 50-node visualization window.
- Clicking a result focuses the exact source-backed entity; selecting a method/function automatically reveals that abstraction level. Follow Build relinquishes focus only after explicit user exploration.
- Search displays the total match count without rendering thousands of rows, adding nested card stacks, enabling new agent actions, or changing the graph's existing 50-visible-node cap.
- Deterministic 4,000-node unit fixture and real Chromium keyboard/button navigation acceptance retain the existing source-only verification contract.

## M8.6 — Python source declarations (initial bounded increment)

- Python `.py` files now contribute read-only, line-numbered class, function, method, `__init__` constructor, and lexical nesting nodes to the same Code Atlas snapshot, alongside existing C# and JS/TS layers. Existing project/task identifiers, source revisions, and commit-pinned imported graphs remain authoritative.
- The managed-code scanner masks comments, quoted text and multiline triple-quoted strings, tracks indentation scopes, avoids executing Python or importing modules, and caps per-file lines and graph nodes/edges.
- This is **structural, not compiler-backed semantic resolution**. Calls, inheritance, decorators, type annotations, runtime dispatch, and dynamic module dependencies are NOT inferred. Unsupported multiline signatures, ambiguous tab indentation, truncated source, or unclosed literals yield explicit partial-coverage diagnostics.
- Preserve independent QA verification and all existing owner-token, approval, source-execution, and cross-task boundaries. Full Python semantic analysis and Java language support remain future M8 increments.

## M8.7 — Java source declarations (bounded static increment)

- Java `.java` source now produces source-backed package/namespace, class/interface/enum/record, constructor, and method nodes with lexical nesting and persisted line numbers.
- The managed-code lexer masks comments, string/character literals and Java text blocks. It never invokes `javac`, restores a classpath, downloads dependencies, loads bytecode, or runs application source.
- Java package/type/member detection is conservative and **structural only**. Record types are currently displayed as classes. Inline type bodies, complex multiline declarations, unbalanced braces, and unclosed literals are explicitly partial; inherited interfaces, resolved method calls, classpath symbols, generics and Java reflection are not claimed as semantic facts.
- Graph size caps, deterministic source revisions, task/commit scoping and all owner/approval/QA boundaries are unchanged. Full Java semantic analysis remains follow-on work.

## M8.8 — Cross-language explicit HTML references (bounded)

- A read-only HTML tokenizer recognizes only quoted, literal local `<script src="...">` and `<link rel="stylesheet" href="...">` references that resolve to **an exact saved JS/TS/CSS file** within the same task/commit revision. Each `MarkupReferences` edge includes its actual source path, line and `LiteralMarkupReference` resolution.
- The parser skips HTML comments and script/style/textarea/template raw-text bodies; it never runs JavaScript, loads web resources, uses a browser/URL resolver, executes npm tools or infers module bundling output. Remote/protocol-relative URLs, credentials, query strings, unsupported file types, unquoted/dynamic attributes, or missing targets never become edges.
- Path escapes, missing local targets, duplicate attributes, malformed HTML, and bounded node/edge/tag limits produce partial-coverage diagnostics instead of imagined links.
- Explicit HTML references are source facts, not proof that scripts load at runtime or that a CSS or JS dependency will build. Existing approval, sandbox, independent QA and identity boundaries are unchanged.
