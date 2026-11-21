# Phase 5 — Independent MAF QA Engineer (static source review)

**Status:** Implementation on PR #127; live Ollama acceptance required.
**Execution:** NONE. This release does not compile, install, run, test or
repair generated applications, and cannot certify PASS.

## Workflow

1. The owner-approved Product Owner specification, both Solution Architect
   decisions, and Technical Lead implementation plan remain authoritative.
2. The fourth MAF Software Engineer produces a terminal source-only run.
   QA can start on **Generated** source, or explicitly review saved partial
   files from a **Failed** or **Interrupted** run. A `Running` run is
   forbidden even when it has already saved files; zero-file runs are also
   forbidden. Failed/interrupted artifacts remain incomplete and unverified.
3. The owner explicitly selects **Ask QA Engineer to inspect generated source**
   (or **Ask QA to inspect partial source**) in Artifacts & Delivery.
   Nothing starts automatically and the previous source generator remains
   untouched. Failed/interrupted engineering source is also available in
   a distinctly labeled **PARTIAL** ZIP.
4. The fifth independent MAF `ChatClientAgent` (`QaEngineer`) gets
   the approved product requirements, architecture, acceptance criteria,
   names/hashes of all source files, and the **complete content of ONE
   engineering source file** per invocation. All findings are explicitly
   source-based hypotheses, not verified defects.
5. For each file, QA returns bounded JSON: a static analysis summary,
   0–6 potential findings with severity/requirement/source evidence/suggested
   action, and 1–6 checks that require later execution or browser testing.
6. The host validates the report, atomically records its SHA-bound evidence,
   and advances to the next file. A completed review is labeled **Reviewed**,
   never `Passed` or `Verified`.
7. The owner may cancel, inspect partial reports or run another explicit
   review. A process restart marks an in-progress review **Interrupted**,
   preserves per-file evidence, and never silently replays model calls.

## Model isolation and role separation

The QA role has a distinct fictional professional backstory, goal, working
style and future skill scopes. QA is not the Software Engineer under a
different prompt. Every QA turn is a genuine MAF `ChatClientAgent` call
to the **exact previously selected loopback Ollama model**, JSON-only,
with no cloud endpoint, no shell/filesystem tools and no unreviewed skill
loader. The model cannot change project source, advance the Technical Lead
plan, or claim an executed test PASS.

The full content of each assessed file is inspected. QA sees **only names,
hashes and purposes** of other files; it must not claim to have inspected
their contents in that same turn. Cross-file integration and functional
behavior must ultimately be verified in a separate executor/QA increment.

## Authentication, binding and persistence

Owner-authenticated local API:

| Method | Path | Meaning |
| --- | --- | --- |
| GET | `/api/tasks/{id}/qa-reviews/latest` | Most recent saved review or null |
| POST | `/api/tasks/{id}/qa-reviews` | Explicitly request review, with `engineeringRunId` and `planSha256` |
| GET | `/api/tasks/{id}/qa-reviews/{reviewId}` | Saved per-file reports, events and status |
| POST | `/api/tasks/{id}/qa-reviews/{reviewId}/cancel` | Stop current QA review |

`qa_reviews` records the private project, source engineering run, source
digest, exact approved plan revision/SHA, specification version/SHA,
architecture SHA, selected model and durable lifecycle timestamps.
`qa_review_files` stores a typed assessment per actual source file and
its SHA. `qa_review_events` records real admission, file review, stop and
completion events. These records are read-only to other agents and are
deleted transactionally with the project after active work stops.

Every admission, per-file report and successful review completion rechecks
within SQLite: source run is terminal (`Generated`, `Failed`, or
`Interrupted`) with at least one immutable saved file, approved specification and
architecture are current, latest Technical Lead plan is still approved,
and the source file digest still matches the original submission.
Changes do NOT silently inherit QA approval or mark old reports current.
Owners can inspect historical evidence but not mistake it for a runtime test.

Saved MAF handoffs contain `QaReviewRequested` and
`QaStaticReportSaved`. These assert only actions that genuinely occurred.

## Acceptance and next phase

Deterministic CI uses an injected no-network MAF `IChatClient` and
typed `IQaSourceReviewer` to prove:
- distinct fifth agent identity and instructions; no tool access;
- invalid `PASS`/malformed schema or inappropriate severity rejected;
- unfinished source and stale plan refused before any QA invocation;
- per-file review of the exact engineering source with durable SQLite
  findings and history; authenticated read and project isolation;
- separate role handoff provenance and no claim of test execution;
- prior engineering/generator/UX regression suites remain green
  on Windows and Linux.

**Owner acceptance after FlowBoard stops:** restart the updated app,
open FlowBoard under Artifacts & Delivery. If the Software Engineer failed
after saving source, inspect its failure category and the partial ZIP, then
select **Ask QA to inspect partial source**. The resulting report explicitly
states **Failed / partial source** and never implies the planned application
was completed. Completed runs retain the regular QA workflow.
QA will report static source concerns and manual
checks; the number of issues depends on the model and source. The next
qualified QA increment must execute generated applications in an isolated,
no-network runtime and capture real build/test/browser results, followed
by a separately authorized repair loop. Security gates must be respected,
not bypassed for the sake of green UI indicators.
