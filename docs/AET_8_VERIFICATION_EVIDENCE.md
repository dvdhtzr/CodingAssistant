# AET-8 — Verification evidence foundation

The existing owner-confirmed, exact-scope Windows container-only smoke
lane now persists its admission, runner report, source fingerprint, scope
digest and plan digest. Restart interruption cannot manufacture a pass.
A new source or changed approval marks old evidence historical.
Contradictory runner results are blocked by the existing strict contract.
The evidence is always Verified=false. No additional runtime or execution
permission is created.

Static Web remains UNQUALIFIED in RuntimeProfileRegistry. The Windows fixed-fixture Chromium compatibility blocker was resolved on the
owner host (11/11 reported at 9d78e08, November 24, 2026). Separate
adversarial runtime qualification and independent functional tests remain required. CI's injected runner checks only
admission/persistence and does not execute or qualify generated source.

## Independent Calculation Journal contract

`tools/m4/calculation_journal_contract.mjs` defines seven mandatory functional checks and executes actual browser-driver operations outside the application page. The candidate app cannot supply the results through a same-origin harness, an HTML marker, or a claimed VERIFIED value. Each scenario starts a fresh private context; the persistence check requires the trusted driver to restart the browser process with the same profile. CI runs the same contract against the frozen working reference, inert reference, and a frozen forged-success script with otherwise working markup. These are trusted fixtures only, not generated code. The module neither launches a browser nor grants execution, and always returns evaluator observations only (runtimeQualified=false, generatedArtifactVerified=false). This is the functional contract foundation for #111, not completion of #76/#92 or a qualified runner. The owner-approved M4.7 seccomp and PID changes were restricted to its fixed compatibility probe; this commit does not extend that approval.

The host-side candidate binder (`static_web_evaluation_candidate.mjs`) validates the exact current task/run/plan/specification/architecture/model/scope selection, the existing bounded ZIP parser, case-sensitive ordered file manifest, SHA-256 and UTF-8 byte counts, and the .NET-compatible source fingerprint. The candidate pins the evaluator source/version, mandatory cases and archive digest. It is immutable, emits no source contents and is explicitly non-executable. The model-free regression tests exercise changed revisions, scope, file bytes, traversal, symlink, extra file and forged status rejection; they are not the ten integrated OS security tests. This module is preparation for the host execution adapter, not an API admission route. The host must independently re-read current scope and obtain separate exact consent before any future execution.

## Trusted integrated qualification runner

The owner explicitly extended the M4.7 fixed seccomp variant and 256 PID ceiling to the trusted Calculation Journal and adversarial qualification only. `windows_aet8_trusted_qualification.mjs --run` stages exactly the four existing Git-blob-pinned reference files in memory, with no source path argument, host mount, model or generated archive. The same disposable guest must pass ten bounded OS negative cases, the M4.7 sandbox checks, seven external-driver functional cases, and inert and forged-receipt negative controls. Chromium uses a private inherited CDP pipe, not a TCP debugging listener or a page-controlled test receipt. No downloads, added capabilities, or changed container limits. The trusted driver sends actual mouse/key events and observes fixed DOM locators; browser restart preserves the private profile. Host validates exact result schema, evidence counts, pinned inputs and cleanup. This is a trusted integrated qualification only, NOT completion of #76 (exact product admission/cancel/recovery still required) or permission to execute generated source. CI tests the host policy, parser and pipe framing; real owner-host execution is required. A failed observation or cleanup blocks. Do not activate the planning registry or product acceptance based on these results.

Owner-host first integrated attempt returned GUEST_REPORT_INVALID and cleanup true; the existing guest discarded all observations on a later exception. Stage-aware, allowlisted failure envelopes now preserve completed security and earlier fixture observations without exposing raw stderr or paths. Browser storage clearing is issued on the attached page target before navigation, not on the browser-level CDP connection; reload waits for a changed document time origin. These changes address a concrete protocol/sequencing defect but the precise original exception is not established by the old report. No policy, resource or authorization changes.

Two owner-host integrated attempts timed out at the 180-second host deadline, with cleanup confirmed and no final receipt. The runner now emits bounded, schema-validated stage checkpoints so a timeout retains completed checks. A 145-second guest watchdog flushes a blocked receipt; host timeout stays 180 seconds. Browser teardown waits for process exit rather than potentially inherited stream closure, bounds graceful and forced shutdown, and rejects a forced shutdown as persistence evidence. Navigation/reload polling now has elapsed-time bounds. Final receipts are flushed before explicit guest exit; container removal is still independently mandatory. No security policy or resource increase. The exact original stuck operation is unconfirmed; these changes correct identified lifecycle/reporting defects.

The owner-host checkpoint reported all 10 security cases passing, the same-guest compatibility check passing, 5/7 working functional cases and BROWSER_SHUTDOWN_NOT_GRACEFUL before negative controls. The trusted integrated runner now uses Docker's private init to reap orphan descendants (no privileges or shared PID namespace), disables Chromium background mode, and gives orderly shutdown up to ten seconds within the unchanged 145-second guest/180-second host deadlines. Forced termination, signal exit, and nonzero exit are distinct blocked outcomes, never persistence passes. The browser-close promise retains failures rather than silently succeeding on a repeat call. The CLI names every unexpected functional case, and the integrated contract propagates driver failures instead of counting them as negative-app rejection; absent fixture controls remain legitimate functional failures. The original exact shutdown cause and two failed case identities remain unconfirmed. No generated execution permission or PID/memory/CPU/network/seccomp policy change.

The next owner-host result again passed 10/10 security and same-guest compatibility, but the strict evaluator stopped on a generic CDP command timeout in WORKING. The transport now reports the specific allowlisted command timeout and checkpoints the exact functional case before each check; readiness polls tolerate bounded transient document-context replacement during navigation. No other timeouts or security limits are expanded. A separate Ubuntu GitHub Actions job now provisions the existing versioned Playwright image in CI and runs the actual trusted integrated guest with the same no-mount/no-network sandbox profile, private-pipe driver and pinned fixtures. This is a real runtime regression, not a mocked Docker success or a substitute for Windows owner-host evidence. The production Windows runner remains offline, Windows-only, and generated execution remains disabled. Linux CI failure is diagnostic and does not establish Windows compatibility failure.

Owner Windows at ee1b1b3: security 10/10, same-guest sandbox compatible, cleanup confirmed; WORKING/history_survives_browser_restart blocked on FIXTURE_NAVIGATION_TIMEOUT. Linux real-runtime CI 45601423246 passed every check. The driver now reuses one page target per process rather than creating and destroying a target for each test. A fresh check navigates to about:blank before clearing the fixed origin and reopening the fixture. Restart still requires previous process exit 0 and a new process using the same profile, without clearing storage, with a bounded 15-second cold-page readiness allowance; host/guest deadlines stay unchanged. Restart target and navigation failures are separate. Case completion is recorded individually with null for not-completed tests, accepted only for blocked/checkpoint evidence; a final successful report must still contain exact booleans. This reduces renderer churn and improves diagnostics but does not establish the underlying Windows failure cause. No sandbox, capability, memory, PID, network or authorization changes.


## Fixed-fixture reset sequencing follow-up

Real Linux trusted-runtime CI at 746cb05 (run 45603851530) stopped in
WORKING/backspace_and_divide_by_zero after three successful functional
checks. All ten security observations, same-guest compatibility and
container removal passed; the failure was FIXTURE_NAVIGATION_TIMEOUT.
The earlier owner Windows evidence stopped at cold browser restart.
Neither establishes the exact reason Chromium failed to navigate.

The trusted driver now clears the fixed fixture origin on its attached
session and performs a real reload for subsequent fresh cases, instead
of repeatedly crossing from the fixture origin to about:blank and back.
This is safe for the immutable reviewed fixtures because they write
history only on explicit actions, not on unload. Each reset must observe
the initial display and zero saved history items. A failed reset or
missing initial display is an infrastructure blocker, never a successful
negative-control observation. Reload requires a finite changed document
time origin and completed document readiness. Cold restart still requires
a clean process exit and a new browser process on the same private
profile without clearing storage.

All pinned fixtures, security cases, functional assertions, guest/host
deadlines, container limits and sandbox restrictions remain unchanged.
This change is restricted to the isolated AET branch. It is a candidate
repair for renderer lifecycle instability, not proof of qualification;
real Linux regression and owner Windows evidence remain required.

## Owner Windows trusted integrated milestone — November 27, 2026

Owner reported `AET-8 WINDOWS_TRUSTED_INTEGRATED_EVALUATOR_PASS` on the
checkout following the 5ff5503 local full regression. Security 10/10,
integrated fixture 5/5, working 7/7, broken 0/7, forged-success 0/7;
all seven observations completed for every control, last guest stage
COMPLETE, container removal confirmed. Owner-local report filename:
`windows-static-web-653ce5730b28.json`. The raw report, exact host/image
identity and digest have not been independently retrieved. This records
owner-observed trusted-fixture success, not a reusable execution grant.

The next increment is the non-executing exact evaluation/admission
contract: current specification, architecture, plan, source manifest,
archive digest, evaluator identity and independently reviewed test
contract, plus host/image/driver qualification identity and explicit
owner consent. Preserve single-use admission, cancellation, crash
reconciliation and cleanup. Keep the existing container-only smoke lane
separate. A fixed Calculation Journal evaluator cannot verify Idea Vault
or arbitrary tasks; contract selection and independent acceptance must
match the approved objective. No generated-source execution until the
remaining #76/#92/#111 security and exact approval gates are satisfied.

## Exact evaluation admission planning contract

`tools/m4/static_web_evaluation_admission.mjs` builds on the existing
candidate binder. It validates the complete fixed-fixture qualification
receipt (not merely a PASS string), exact security/positive/negative
observations, cleanup, local image identity/digest, fixture bytes, driver,
evaluator, guest payload and seccomp pins. It requires explicit contract
selection and a mapping of every current approved acceptance criterion to
reviewed mandatory evaluator cases. A fixed calculator contract is not
automatically applicable to another static-web project. The host must
supply the full current criteria and a human-reviewed semantic mapping;
structural coverage alone cannot establish semantic correctness.

The resulting digest pins the existing candidate, exact archive, scope,
source, approvals, evaluator, qualification receipt and coverage mapping.
A planning acknowledgement must match a freshly reconstructed descriptor.
Both preview and acknowledgement are permanently non-executable. A receipt
from disk is not host attestation; actual host provenance, durable owner
execution consent, single-use transactional admission, cancellation, crash
cleanup and actual candidate-runtime qualification remain separate gates.
No new route, runtime capability, process invocation or registry promotion
is introduced. Model-free tests cover incomplete/stale/contradictory
qualification, changed image identity, incomplete contract mapping and
stale/forged acknowledgement, not actual candidate execution.
