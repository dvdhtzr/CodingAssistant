# Mission Control — Engineering Workbench UX contract

**Direction:** Approved by the human owner, November 20, 2026.
**First slice:** UX-1, project-centered application shell.
**Audited baseline:** GitHub main f935c0652a72f45cc784fadaace7b040b6d3b9c7 (PR #118).
**Status:** Approved target design; not every feature is implemented.

## Goal and principles

Mission Control is a local-first project environment where a human owner can define work, collaborate with specialized AI engineering participants, review decisions, inspect code, and trust independent evidence.

Design for project continuity, human authority, distinct agent responsibility, inspectable work products, and truthful evidence. Do not mistake fluent model text or generated source for a successfully compiled and verified application.

## Current capabilities versus future target

| Area | Current product | Future workbench |
| --- | --- | --- |
| Product Owner | Local Ollama interview, saved transcript, versioned specification, explicit approval | Dedicated conversation, handoff context |
| Solution Architect | Proposed alternatives and tradeoffs, versioned approval of stack/style | Decision inbox and review history |
| Source | Architecture-bound generated source text and downloadable ZIP, not tested | Read-only file tree, diffs and provenance |
| Execution | Historical isolated fixed C# repair pilot with separate approval | Qualified task-specific runtime profiles |
| Team | Existing workflow roles, no general MAF-backed roster | Dynamic identities, assignments, skills and handoffs |
| Activity | Some SQLite event replay and SignalR hints; other polling | Unified durable timeline |
| Verification | Pilot evaluator evidence; ZIPs remain unverified | Independent build/test/browser/security evidence |

## Information architecture

**Global header:** product identity, local status, host execution capability and session action. No secrets shown.

**Project sidebar:** create project, saved projects, Overview, Engineering Studio, Legacy diagnostics. UX-1 contains only working destinations: do not make dead navigational links for future views.

**Overview:** arbitrary user objective, clear continuation, persisted specification status, architecture readiness and saved source draft state. Fetch authenticated existing APIs. Unavailable data differs from not-yet-completed data.

**Engineering Studio:** keep the current Product Owner, Solution Architect, project-draft and task-scope flow operational. Existing server contracts remain intact; later slices split into Conversations, Decisions, Code & Artifacts, and Activity.

**Legacy diagnostics:** present the single HTML generator and isolated C# qualification flow clearly as historical/advanced tests, not steps of the owner's current project.

**Inspector:** project name/record, current screen, legacy run state where present, local inference and host execution capability. Never claim unsourced agent activity.

**Team:** initial data-driven presentation of available workflows versus explicitly planned future roles. Do not derive false live-agent state from labels; future MAF integration supplies a variable-length server registry.

### Desktop layout

Three regions: 235px project/team sidebar, flexible central task canvas, 245px inspector. On medium screens collapse inspector. At narrow widths put the navigation above content and hide informational sidebar lists. Desktop engineering usage is primary; preserve accessible compact layouts.

## End-to-end user journey

1. Owner creates an arbitrary project objective and answers adaptive Product Owner questions.
2. Owner reviews and explicitly approves versioned specification content.
3. Solution Architect recommends stack and architecture alternatives; owner reviews and chooses, independent of model preference.
4. A future Technical Lead produces an explicit bounded plan and hands work to the right agents.
5. Software Engineer generates source. Executable work requires separately approved scope and host qualification.
6. Independent evaluation records build/tests/browser/security results; QA and Security may discuss evidence but cannot confer VERIFIED merely by assertion.
7. Owner inspects artifacts/evidence, downloads, or requests bounded repair.

Navigating back should never silently invalidate or reapprove old work. Changes to spec or architecture must visibly mark descendants stale; current backend rejects invalid approval hashes.

## Agent/work-product model (future, not an existing API)

An agent participant has stable identity, role, exact local model, stated responsibilities, skills/capability descriptors, observed activity state, assignment and work products. The UI must support any number of participants.

Distinguish messages, system events, agent proposals, owner approvals, handoffs, generated files, and independent verification reports. Trace provenance to exact task, source version, model, and event sequence. MAF may orchestrate reasoning; existing secure execution boundaries control tools and code. Skills are reviewed descriptors first, not privileges.

Status vocabulary: not started, waiting for owner, working (confirmed activity only), cancelled/failed/interrupted, generated-not-tested, build-failed, tests-failed, verification-blocked, verified-with-independent-evidence. No invented progress percentages or model heartbeat.

## Design system

Restraint over decoration: ink canvas #0D121B, surface #172231, borders #35485A, green action #7DD3BE, amber attention #E7B777 and failure #DC7985. Semantic status text accompanies color. System/Inter UI font; monospace for source, logs and hashes. Spacing 4/8/12/16/24/32. Accessible focus, keyboard operation, contrast and reduced-motion support.

## Delivery milestones

- **UX-0 and UX-1:** Approved design contract, responsive project shell, persisted project overview, honest team labels, diagnostics separation; no API change.
- **UX-2:** Conversation workroom and browser-level navigation/interview regression tests.
- **UX-3:** Decisions and owner approval inbox, with original exact server-bound approvals preserved.
- **UX-4:** Read-only source/artifact inspection and provenance.
- **UX-5:** Real MAF roster, assignments, handoffs and skill descriptors.
- **UX-6:** Qualified execution and verification timeline; security issues #76 and #92 remain release blockers.

These UX slices augment, not reorder, Development Plan v2 phases 0–5. Each slice goes through branch, PR, cross-platform deterministic checks, owner live-model acceptance where relevant, merge and main verification.

## UX-1 acceptance and regression

- New project and saved-project selection use existing authenticated task endpoints.
- Existing Product Owner model selection, interviews, spec approvals, architecture choices, generator, ZIP and task-scope review remain reachable in Studio.
- Studio stays mounted when switching tabs so an unsent answer or in-flight view state survives navigation.
- Switching projects resets task/run selection and opens that project's Studio.
- Legacy HTML and fixed-C# pilot remain usable only through explicitly named Diagnostics.
- Windows same-origin automatic session, Linux manual owner token, in-memory bearer, approval gates, and SQLite run replay remain unchanged.
- Project Overview shows only persisted data; generated source is NEVER called verified without independent evidence.
- Run TypeScript/Vite build and existing cross-platform .NET real-HTTP/SQLite smoke tests before merging.

## Explicit limitations

UX-1 does not add React Router, deep links, a unified event store, automated browser UI tests, agent execution or MAF integration. The overview is a saved snapshot fetched on opening, not a live heartbeat. UX-2 will add browser-level interaction tests; UX-5 and UX-6 require their corresponding real backend milestones.

## UX-2 Project Journey accordion (implemented)

The Engineering Studio now presents six independently status-aware stages:
Product Discovery, Specification, Solution Architecture, Implementation Plan,
Source Generation, and Artifacts & Delivery. Exactly one stage body is visible. Switching stages
uses persistent mounted components, rather than unmounting local interview
drafts, Architecture choice forms, or draft-generation polling. Every header
is a keyboard-focusable button with aria-expanded and a stable labelled
content region. A collapsed stage remains accessible by clicking its header,
including a stage whose prerequisites are not met (to explain the blockers).

Header bands use subdued colors alongside explicit text: green for complete,
teal for active, amber for human review, slate for pending, and red for blocked
or stale. Completion means that stage's *own* work is complete. The generated
source and ZIP are explicitly marked unverified; no colored band implies
successful compilation or evaluator approval. A compact status strip summarizes
saved work. Percent-complete and synthetic model heartbeat are prohibited.

The Product Owner transcript is separately scrollable and the answer composer
remains accessible. The open stage has a bounded scroll region. The existing
Product Owner model picker, full conversation, specification content and SHA
approval, Architecture proposals/choices with exact confirmation, draft
generation/cancellation, source inspection, ZIP download and task scope review
retain the same API endpoints and authorizations.

Successful specification drafting opens Specification; successful approval
opens Solution Architecture; the second approved architecture topic opens
Source Generation; an *observed* Running-to-Generated transition opens Artifacts
only when the owner is still viewing Source Generation. Returning to a saved
project defaults to the next useful stage. After explicit manual navigation,
background polling cannot steal focus or change the expanded stage.

The existing legacy diagnostics and workspace navigation remain outside the
accordion. This stage UI is not a new task engine or live MAF agent registry.
The five current stages are declared as data, allowing later server-driven
stages and work products without tying the layout to fixed numbers.

## Follow-up: optional zero-open state and owner project deletion

A second press on the current accordion header closes its stage. The initial
saved-project view still restores one actionable stage, and no other stage is
opened automatically after deliberate manual collapse. The progress strip
shows "All stages collapsed", not a nonexistent Stage 0.

Each saved-project row includes a visible Delete action, followed by a separate
confirmation explaining that this is permanent. The authenticated owner DELETE
endpoint removes the selected task, interview/specification history, architecture
decisions, generated draft files and events, HTML artifacts, pilot scope reviews,
plans, run intents and execution event history, atomically with SQLite FK checks
enabled. Other projects are untouched. Active generation or execution blocks
deletion with a conflict until cancelled/completed. The UI clears a deleted
project selection but does not delete any workspace directory or Git repository.

## UX-2 follow-up — focused Product Owner workroom and interactive stage rail

The current saved Product Owner message is presented as a focused question
above the existing radio/checkbox/free-text composer. Previous persisted
assistant/owner turns are retained in an accessible, expandable history with
a bounded independent scroll region. No messages are summarized, truncated,
modified or fabricated. The current model, 12-answer maximum, owner control,
snapshot sequence and specification approval continue unchanged.

Each of the six actual journey stages now has a keyboard-accessible progress
tile that opens the corresponding existing accordion panel and scrolls to its
header. Direct selection never collapses an already-open stage and takes
priority over background status refresh. Original accordion buttons can still
be used to deliberately collapse a stage. Stage indicators reflect only
persisted state; they do not estimate model progress.

Regression coverage includes model-free React render/escaping checks and
genuine Playwright/Chromium browser interactions (UX-2 fixture):
- Reading the current question and expanding a long, independently scrolling
  saved transcript.
- Navigating between Specification and Discovery with keyboard and mouse.
- Preserving unsent multiple-choice selections and Something else text across
  a stage change, then exercising the composer submission callback.
- Accessible panel association and navigation state across all six stages.

These browser tests mount the actual React UI components against controlled
fixtures. They do not replace owner live-model acceptance, exercise the
authenticated .NET backend, or claim generated source is tested. The CI
workflow runs real browser checks on Ubuntu, and existing frontend/HTTP/SQLite
checks on both Ubuntu and Windows.

## UX-3 — read-only owner decision and approval inbox

Engineering Studio now includes a small owner inbox above the six existing
accordion stages. It reads only the existing authenticated interview,
architecture decision and implementation-plan records. It distinguishes:
- Draft or superseded specifications requiring exact owner review;
- Proposed technology-stack/architecture-style options requiring explicit
  alternatives review, and missing/deferred recommendations;
- Stale architecture proposals bound to an older approved specification;
- Proposed, missing or stale Technical Lead implementation plans;
- A fully cleared approval ledger, which still does **not** mean the generated
  source is verified.

The inbox never writes approvals, bypasses prerequisite guards, or copies
confirmation controls. Clicking its actionable items navigates to the
**existing** specification, architecture or plan stage. The original SHA-256
confirmation and per-stage owner gates remain authoritative. If any of the
three reads fails, the inbox reports a load error rather than displaying a
misleading 'all approved' state.

Status derives deterministically from the saved records. React model-free
tests cover approved, proposed, missing, deferred and stale states; a
Playwright browser fixture verifies inbox navigation enters the real
Architecture panel without generating approval. This is not a claim of
live-model behavior, build/test verification, or owner production acceptance.

## UX-4 — Source & Artifact Explorer (MAF and classic generation)

Artifacts & Delivery now shows a shared, **read-only** source explorer for
both the real MAF Software Engineer run and the distinct classic-generator
fallback. It consumes each workflow's *existing authenticated saved snapshot*;
it does not introduce file-system traversal, arbitrary file fetches, a new
artifact store, execution privileges or approval mutations.

The explorer provides a nested, keyboard-accessible saved file tree, filename
and purpose filtering, selected-file source display with line numbers,
language labels based on filename, source path and content copying, and
provenance: saved purpose, exact SHA-256, timestamp, and originating MAF work
item (where recorded). Classic drafts explicitly say that per-file task
provenance was not recorded. It also shows whether the source is current with
the saved approval binding, and distinguishes Generated from partial,
interrupted and failed source. The MAF ZIP and classic ZIP download controls
remain in their original stages; static QA inspection stays separate.

For performance, the initial numbered source preview is limited to 750 lines;
a real "Show complete file" control reveals the entire saved content when
needed, and "Copy source" always copies the complete text. The viewer never
evaluates HTML, JS, or code. File text is rendered as escaped React text, not
as DOM from the source file. Responsive laptop/mobile layouts use one or two
columns as space permits; code panes scroll without trapping navigation.

The new model-free tests validate file hierarchy, provenance, safe rendering,
empty and partial source states, long previews, and existing CSS integration.
Chromium interaction tests exercise actual file selection, search, provenance,
source escaping and full-file reveal, on top of the existing UX-2/UX-3 tests.
Cross-platform frontend/API/SQLite and Linux browser CI remain required.
This milestone does **not** implement file diffs or independent verification;
those require separately grounded historical source and evaluator evidence.

## UX-5 — Real MAF team workroom and skill scope descriptors

Engineering Studio now has a collapsible Team Workroom beside the existing
decision inbox. It reads the existing authenticated agent-roster,
agent-handoffs, engineering-runs/latest, and qa-reviews/latest endpoints;
there is no fabricated agent thinking or new orchestrator in this slice.

Each immutable, host-defined Product Owner, Solution Architect, Technical
Lead, Software Engineer, and QA Engineer profile has an individually
selectable, keyboard-accessible identity card. The selected profile
displays mission, working style, fictional backstory, read-only declared
future skill scopes and the fact that no SKILL.md package is installed by
the view. Displaying a scope never enables execution or grants permissions.

Saved handoffs are filtered to the selected participant with actual
timestamps and current/historical binding labels. The Software Engineer
shows its latest persisted run and per-task states/summary; completed tasks
mean source saved rather than tested. QA displays its saved independent
static review, file report count, and whether it references the latest
recorded engineering run and plan hash; it does not certify tests.
Unrelated role cards show recorded handoff counts, not inferred presence.
Links open the original owner-controlled journey stages, without approval.

To minimize resource use on small local hosts, the panel fetches data
only while expanded, polling saved records every eight seconds.
Errors clear evidence rather than treating unknown data as no work.

Regression coverage includes deterministic React checks of independent
identities, counts, historical bindings, skill limitations, empty history,
engineering/QA states; Chromium exercises role selection, task evidence,
QA historical warnings, and stage navigation without approval.
Both Windows/Linux UI and API/SQLite checks remain merge prerequisites.

UX-5 is not a pluggable SKILL.md loader, executor or independent test
runner. Local skill packages need a later host-admitted, versioned,
audited capability contract. UX-6 requires independent build/test
evidence and security qualification before VERIFIED claims.

## UX-6A — Persisted verification evidence timeline (read-only foundation)

A collapsible **Execution & Verification Timeline** in Engineering Studio reads
only existing authenticated host records:
`engineering-runs/latest`, `qa-reviews/latest`,
`implementation-plan`, and `runtime-profiles`. It renders chronological,
persisted per-run engineering and static-QA events with timestamps, record
sequence numbers, provenance and current/historical plan binding. It does not
infer progress from model prose or fabricate a source generation, test or repair.

Three separate evidence lanes distinguish source-generation attempts,
model-based static QA inspection, and independently qualified build/test/browser
evaluation. Source is always **GENERATED · NOT TESTED** or a clearly marked
partial/historical state, regardless of static-QA conclusions. The
`static-web` runtime descriptor remains **UNQUALIFIED**, non-executable and
subject to security gate #76 and task objective gate #92. Missing or
unreadable evidence is not treated as a test failure or a passing test.

The current UI intentionally shows only latest MAF engineer/QA histories;
older episodes remain in existing per-run stores, and the separately scoped
legacy M1 C# pilot is not presented as a task-specific project verifier.
No new API endpoint, execution permission, approval contract, evaluator,
model call, or browser runner is introduced. Completing UX-6 requires
separately persisted, independently produced verification/repair evidence
after #76/#92/#111 acceptance. Existing browser and model-free React tests
cover chronological ordering, historical bindings, empty/failed states,
source escaping, honest qualification labels and UI interaction. Owner
Windows browser review remains separate from CI.

## UX-6B — Deterministic Static Web source preflight (model-free)

The authenticated GET
`/api/tasks/{id}/engineering-runs/{runId}/static-web-preflight`
reads only the exact task/run's existing saved source snapshot, subject to
existing owner authentication. It never calls Ollama, executes scripts,
parses with Node, starts Chromium or a container, grants scope, or writes
records. The bounded host C# scanner checks HTML script/style references,
relative path containment, missing linked files, comments-only inline
scripts, missing `.js` entry links and simple literal DOM IDs/classes
referenced from saved JavaScript. It does not parse the full JS/HTML grammars
or validate complex/dynamically created elements; these are potential
findings, **not test failures**.

The report includes source SHA-256 (using the same stored-file fingerprint
as static QA), run ID/state, source-check status and explicit
`verificationStatus=GENERATED_NOT_TESTED` for *every* outcome. A
`NO_ISSUES_DETECTED` result means only the narrow heuristics found no
issues, **not** that a browser application works. Running/empty/non-HTML
source has its own non-verification status; stale artifacts retain their
original provenance. Mission Control renders the report in the existing
Execution & Verification Timeline, including failed/partial source, and
does not pretend Static Web runtime admission is qualified.

The model-free acceptance suite includes the documented Calculation
Journal unlinked-script/selector failure from #107, healthy linked
HTML/CSS/JS, remote/path traversal refs, non-HTML and in-progress source,
and authenticated task/run-scoped real HTTP checks. #76, #92 and #111 remain
required for *actual* independent Node/Chromium execution, bounded
repair, signed test evidence and VERIFIED artifacts. This is the static
source preflight foundation only, not qualification of the runtime profile.

## UX-6C — Windows-first Calculation Journal real-browser regression

The [trusted Chromium fixture harness](WINDOWS_STATIC_WEB_ACCEPTANCE.md) now runs both broken and working hand-authored calculator fixtures in real Chromium on **Windows and Ubuntu CI**, with actual mouse, keyboard, History and browser-restart persistence checks. This qualifies the acceptance *harness*, not the current generated ZIP, untrusted JavaScript, or Windows Docker Desktop isolation. No production executor or VERIFIED status is enabled. The optional Windows Docker Desktop metadata doctor is read-only and cannot authorize execution.
