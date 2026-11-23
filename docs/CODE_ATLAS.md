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

## M8.9 — Explicit Python module source links

- Index top-level, single-line `import package.module` and `from .module import Symbol` / `from package.module import Symbol` references only when the module maps uniquely to an **already saved** `.py` or `__init__.py` file. No Python interpreter, import loader, package installation, sys.path assumption or filesystem access is permitted.
- Each `ModuleImports` file-to-file edge has exact source path and line and the explicit `ExplicitPythonModuleSource` provenance. It is only an import statement with a matching indexed source file, not proof of successful runtime import or use of a specific symbol.
- Standard-library/external names, dynamic imports, function-local imports, comments, strings, ambiguous module matches, and unresolved/escaping relative references produce no guessed edges. For ambiguous, missing or escaping **relative** modules, report partial indexing with bounded diagnostics.
- Fully preserve task/commit isolation, owner authentication, deterministic graph revisions, existing caps, and the independent verification/approval boundary.

## M8.10 — Explicit Java type-import source links

- Recognize top-level `import package.Type;` declarations only when the qualified Java type is uniquely declared in another accepted Java file of the **same immutable source graph**. Resolve against parsed package/type declarations, not source-directory naming conventions or a classpath.
- Emit directed file-to-file `ModuleImports` edges with actual source file, line and `ExplicitJavaTypeSource` evidence. Path-tracing, replay and imported-commit diff can reuse the existing graph contract.
- Static imports, wildcard imports, external and standard-library types, comment/string decoys, missing types and multiple conflicting local declarations do not become guessed links. Duplicate matching package/type declarations are diagnosed as partial coverage.
- This is **source-level dependency evidence, not javac-backed symbol resolution or proof that an import compiles**. No Java code, shell, package, build, classpath or project configuration is executed or trusted.

## M8.11 — Named JavaScript/TypeScript import-to-symbol navigation

- M8 now links `import { Symbol as Alias } from './local-module'` and `import type { Interface } from './local-module'` directly to an **indexed, uniquely named, directly exported** class, interface or function in the exact saved source snapshot. Named arrow-function declarations are included.
- Each `SymbolImports` file-to-symbol edge has its importing path/line and `ExplicitLocalNamedExport` resolution. Existing bounded path tracing, replay and commit drift can navigate or compare the links.
- Local module resolution reuses the original M8.4 safe literal path logic. Dynamic imports, package imports, missing or ambiguous local modules, implicit/default exports, namespace imports, re-exports and private/non-exported declarations never produce guessed symbol links. Duplicate ambiguous export declarations result in partial-coverage diagnostics.
- This is **deeper source-backed symbol resolution**, not the TypeScript language service. Inheritance, arbitrary callers/callees, type inference, computed exports, dynamic bindings and runtime invocation remain unverified and are reserved for later qualified semantic work.
- No untrusted JavaScript, dependency installation, build, network fetch or process execution is introduced. Existing graph caps, source revision identity, independent QA and owner approval/security boundaries remain intact.

## M8.12 — Conservative JavaScript/TypeScript type-clause relationships

- Source-backed `Inherits` and `Implements` edges now connect file-scoped JS/TS classes and interfaces for explicit bare-name `extends`/`implements` clauses, including unambiguous named import aliases and direct, uniquely indexed local declarations.
- Edges carry exact source path/line and `ExplicitEcmaTypeClause` provenance. Existing read-only path tracing and imported-commit drift comparisons can use them.
- Ambiguous or repeated imports, duplicated declarations, wrong symbol kinds, private imported types, class expressions, computed bases, generics, unknown external symbols and dynamic factories remain unlinked. Arbitrary method/call binding is **not** claimed.
- These are conservative source facts, **not TypeScript language-service semantic results or proof of valid build/runtime inheritance**. Full compiler-backed resolution and caller/callee analysis remain future work. Existing graph limits, task isolation, owner approval, QA boundaries and no-execution policy are unchanged.

## M8.13 — Bounded explicit JavaScript/TypeScript caller-to-callee evidence

- Recognizes unqualified, direct statement-form `helper();`, `return helper();`, and `await helper();` invocations inside **zero-argument, top-level named function bodies**. Links to one uniquely declared same-file function or an explicitly named value-import of a directly exported function from the same saved graph.
- Each `Calls` edge records its actual call-site path/line and `ExplicitEcmaDirectCallSyntax` provenance. Existing read-only dependency tracing and architecture drift can reuse these edges. The edge means the source contains a directly identifiable call expression, **not** that the call was executed or compiler/type-checker verified.
- No attempt to resolve receiver/property calls, methods, dynamic imports, generic invocations, nested callbacks, arrow-function caller bodies, function parameters, overloaded/duplicate names, type-only imports, arbitrary expression calls, optional chaining, or module aliases.
- Local shadowing/rebinding, unsafe eval/with/destructuring scopes and ambiguous local or imported declarations fail closed. Source files remain data: there is no JS evaluation, filesystem fetch, package installation, script execution or new sandbox permission.
- Limits, deterministic revision identity, independent QA and owner approval/security boundaries are unchanged. Compiler-backed TypeScript/JavaScript calls and richer semantic coverage remain future milestones.

## M8.14 — Safe parameterized JS/TS callers and immutable arrow callees

- Extends M8.13 direct statement-form call evidence to named top-level function callers with simple parameter lists: bare identifiers, optional `?`, and one-token TypeScript type annotations, including a simple one-token return type. Each parameter name shadows any same-named local or imported callee.
- Uniquely indexed `const name = (args) => ...` or `const name = arg => ...` declarations may now be direct callees from the same file or through a uniquely named, directly exported, non-type import. File-level reassignment or ambiguous bindings disqualify a target, including across files.
- Unsupported defaults, destructuring, rest arguments, generic/union/qualified parameter annotations, arrow-function caller bodies, nested callbacks, and receiver methods remain unlinked. No runtime dispatch or TypeScript language-service analysis is claimed.
- `Calls` retains `ExplicitEcmaDirectCallSyntax` path/line evidence, deterministic graph source revision and bounded path tracing. No package installation, code execution, network fetch, sandbox elevation, approval changes or merge to `main`.

## M8.15 — Explicit Java class/interface type relationships

- Resolve top-level Java `class Child extends Base implements Ready` and `interface Derived extends Parent` clauses, including multiple comma-separated direct interfaces, only when their bare-name targets map uniquely to accepted source declarations.
- Resolution considers declared Java packages and exact, non-static, non-wildcard import statements. Links are directed `Inherits`/`Implements` type-to-type edges with actual source path/line and `ExplicitJavaTypeClause` provenance. Existing bounded path tracing, replay and drift use the same graph schema.
- Duplicate declarations, competing same-package/imported types, wrong target kinds, generics, qualified/computed bases, missing/external dependencies, wildcard-only imports, comments and strings never become guessed links. Unsupported type clauses and ambiguities report bounded partial-coverage diagnostics.
- This is **source-level structural evidence, not javac/classpath semantic binding or proof of a successful build or runtime type relationship**. No Java compilation, shell, package installation, source execution or unqualified runtime tracing is introduced. Existing task isolation, independent QA, owner approval and `main` merge boundaries remain unchanged.
