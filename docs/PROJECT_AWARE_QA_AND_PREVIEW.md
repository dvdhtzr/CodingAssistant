# Project-aware QA and Application Preview

Status: owner-approved design direction; incremental implementation.
Related issues: #107, #111, #76, #92. Preserve the existing separate execution approval, source-only generation, and Windows qualification gates. The autonomous engineering team feature branch remains isolated.

## Motivation

The Idea Vault owner-local trial reached independent QA but reported almost the entire specification as possible defects across index.html, scripts.js, and styles.css. The existing per-file review received the full specification but only one file's text; a missing implementation in the assigned file does not establish a missing feature across the application. There was no interactive preview or runtime evidence. These are observed review limitations, not proof that all listed functionality is broken or working.

## Milestones and acceptance

1. **Project-aware static QA.** Retain the per-file persisted report and repair boundary while supplying bounded, immutable same-run companion HTML/JS/CSS context for approved Static Web projects. Review the relationship between linked assets, controls, selectors, handlers, persistence and styles. Attribute actionable findings to the source file containing the cause, not every file implicated in the requirement. Missing or truncated context and dynamic behavior are manual checks. Larger projects need an explicitly bounded host-brokered project investigation mechanism before claiming comprehensive review. No arbitrary file reads, imported repository content, callable native tools, execution or network authority. Acceptance: model-free fixtures for linked source, missing links, scope, omitted companions, exact-source matching, repair re-review, and existing API behavior; owner-local review of the existing Idea Vault snapshot. 
2. **Consolidated acceptance and findings.** Bind stable acceptance criterion identities to the approved plan/spec and source fingerprint; separate implementation evidence, concrete static inconsistency, not implemented where supported, and requires testing. Keep uncertainty visible and do not infer runtime success from static evidence. Consolidate overlapping reports; use specific evidence and owner-approved bounded repair scope, not repeated restatement of the specification. Acceptance: same feature referenced in HTML and JS yields one actionable cause; unknown behavior cannot silently become an approved defect; old snapshots remain historical.
3. **Independently qualify Static Web runtime.** Complete the trusted fixed-fixture Windows and Linux evaluator and relevant integrated adversarial gates in #76/#111; pin images, enforce disposable guests, deny host/secrets/network/process escapes and verify cleanup. CI/model-free or a successful trusted fixture alone never admits generated source.
4. **Application Preview destination.** Show the actual generated application only in a separately qualified isolated guest after explicit exact-scope approval. No generated HTML/JS inside the authenticated control-plane origin, no host-browser file launch, no untrusted code before the runtime gate. Read-only source and static inspection stay available when preview is blocked. Provide revision identity, clear unverified status, stop/cleanup and user feedback. Acceptance: a preview cannot reach owner credentials, control-plane endpoints, host files, unrelated projects, or external network; lifecycle is bounded and teardown verified.
5. **Independent acceptance checks.** Use host-owned, bounded browser/Node tests with observable actions and results tied to stable criteria, including create/search/edit/delete/persistence for Idea Vault and the frozen Calculation Journal regression. Distinguish passed, failed, not run, blocked. No model-generated evidence. Test definitions must be reviewed, source-bound and independently executed within qualified runtime.
6. **Evidence-driven repair/retest.** Feed actual failed assertions and exact source evidence into the Software Engineer; retain existing owner approvals, bounded scope, cancellation, restart recovery, independent re-evaluation and truthful artifact status. Do not relax the execution contract or silently grant additional file writes.
7. **Engineering Studio integration.** Make Application Preview prominent alongside team, Code Atlas and verification. Show meaningful acceptance progress, current revision and actual blockers, not extra nested reports or cosmetic success states. Keep existing source export and historical evidence accessible.

## First implementation slice

The initial PR extends the existing real MAF QA adapter with optional project companion evidence: at most six complete companion files and 4,800 serialized characters of companion data, further constrained by the existing prompt budget. Files omitted due to budget or outside allowed web-source extensions are explicitly identified when the envelope fits; otherwise the policy treats all companions as uninspected. The primary file remains exact-snapshot bound and the existing Code Atlas broker retains its one-file limit and two-request cap. The agent has zero callable functions. It must route uncertain observations to manual checks, rather than automatically treating every feature missing from a primary file as a defect.

This is not an entire project-level acceptance engine, a semantic proof of defect correctness, or an interactive preview. Model-free tests establish the context and restrictions, not that a local model follows every instruction. The original per-file report and owner-confirmed repair API are preserved. Subsequent increments must add enforceable evidence classification/consolidation and the qualified runtime.


## Second implementation slice — plan-bound acceptance evidence

The saved QA snapshot adds a read-only acceptance projection, built from the
exact formerly approved plan revision and the review's immutable source
fingerprint. It checks saved file identities and report hashes in a single
SQLite read transaction, including historical plans. Criterion IDs are
`work-item-id:ordinal` within the immutable plan SHA-256 namespace.
New QA prompts include these IDs and ask the model to copy the corresponding
criterion text; existing reports remain readable. The projection links an
observation only when its requirement text matches exactly (case and whitespace
normalized) and the identity is unambiguous, or a supplied ID disambiguates
that exact text. Unknown IDs, ambiguous matches, and paraphrases remain
unmapped observations rather than invented coverage.

Identical same-file observations (requirement, evidence, recommendation and
resolved criterion) are grouped for presentation and repair selection. The
highest-severity original observation represents the group; its exact original
path/index remains the repair identity. Different files, evidence or proposed
actions are deliberately not merged. Raw reports and the existing durable
proposal/triage/admission contracts remain unchanged. The repair UI now displays
the actual evidence and recommendation and resets selection when the project,
review, source or plan changes.

Every criterion still requires testing. A linked static concern is a hypothesis,
and no linked concern does not establish implementation. This slice does not
validate the semantics of model evidence, automatically classify absence or
implementation, merge paraphrased defects, perform tests, or qualify a preview.
These are subsequent increments. Model-free tests exercise exact historical
bindings, ambiguous and invalid identity mapping, duplicate consolidation,
and the authenticated persisted QA response.
