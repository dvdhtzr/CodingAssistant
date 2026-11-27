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

## Durable non-executable evaluation plan review

The host-only `EvaluationPlanReviewStore` captures the latest exact
approved source and all plan acceptance criteria transactionally. Its
separate evaluation ZIP uses ordinal paths, fixed timestamps and no
compression, so the archive digest does not change merely because a
preview is regenerated later. Ordinary source export is unchanged.
The store is a persistence seam for trusted Node binder output, not an
HTTP route accepting model or owner-provided descriptors. It rechecks the
current task/plan/specification/architecture/scope/archive binding and
criterion identities, retains the exact preview JSON and its own digest,
records one exact planning acknowledgement, cancellation, and interrupted
unreviewed proposals on startup. Identical replay cannot reactivate a
cancelled or interrupted receipt. It does not independently authenticate
the Node qualification receipt or prove semantic test coverage; that is
still the trusted binder/host integration's responsibility. No executable
authorization, worker dispatch, candidate-runtime or container cleanup
lifecycle is implemented by this storage increment. Model-free smoke tests
use synthetic preview descriptors, not real runtime evidence.

## Host-only planning bridge

The C# `EvaluationPlanBinder` captures current task inputs, sends a bounded
JSON request over stdin to the fixed Node `static_web_evaluation_host_bridge.mjs`,
and stages only the returned non-executable descriptor after rechecking the
current SQLite bindings. Node reads only the named qualification receipt in
the owner-local qualification directory; callers cannot provide arbitrary
paths, Docker metadata, a receipt body or a command. Only local Windows
Docker Desktop metadata queries are allowed. The bridge never launches a
container or generated source, grants execution, or establishes trusted host
provenance from a file alone. No HTTP route is added. The owner can inspect
the existing receipt without repeating the fixture run:

~~~powershell
node tools/m4/static_web_evaluation_host_bridge.mjs --inspect-receipt windows-static-web-653ce5730b28.json
~~~

This checks current image, evaluator, driver, seccomp and fixture binding.
It remains an inspection, not qualification of the execution adapter.
Model-free tests use injected metadata and receipt input, not actual host
qualification. Node process launch/timeout and actual C# integration require
local validation; executable admission and cancellation/cleanup are still
separate future gates.


### Explicit planning process identity

The internal C# binder now requires an explicit absolute repository root,
absolute local Node executable path, and independently established SHA-256
pins for the Node executable and fixed binder entry script. It no longer
searches the working directory or parent directories for the script, or
launches Node by an unqualified name. Missing, changed, relative or
reparse-point paths fail closed. Node-specific and OpenSSL configuration
environment overrides are removed from the child process. The direct
owner CLI receipt-inspection command is unchanged.

No request-facing configuration, HTTP route or model tool is introduced.
These file checks do not authenticate the transitive Node module graph,
Docker CLI or current host capability, and do not protect against an
already-compromised owner account or concurrent same-account file changes.
Establishing a trusted host-owned configuration source and validating the
full process/dependency provenance remain prerequisites before any
executable admission. The C# binder must not be enabled by deriving pins
from unreviewed files at request time.

### Planning dependency and Docker metadata identities

The C# planning lane additionally requires an exact host-owned pin set for
the twelve executable local imports of the binder, and an absolute local
Docker CLI path and SHA-256. It rejects absent, extra, or modified module
pins before starting Node. The stdin lane verifies the Docker executable
again before each fixed metadata query and never looks it up through PATH.
The direct owner receipt-inspection CLI is unchanged and is not an
attestation mechanism. This still requires independently established,
trusted configuration and owner-local end-to-end validation; pins cannot
establish host integrity or eliminate same-account file replacement races.
No execution authority or Docker run path was added.

### Owner-private planning configuration

The internal `EvaluationPlanningHostConfiguration` reads only
`evaluation-planning-host.json` from the already owner-private control-plane
data directory. It reuses the existing directory/file privacy checks,
rejects redirected ancestors, missing files, oversized or malformed JSON,
duplicate/unknown fields, relative paths and incomplete module pins.
It provides a factory for the existing planning binder, not a new API route.
No configuration is generated, written, enrolled or activated automatically.
A missing configuration blocks planning through this factory.

Schema 1 requires repositoryRoot, nodeExecutable, nodeSha256, binderSha256,
modulePins (the exact twelve names), dockerExecutable and dockerSha256.
All digests are lowercase SHA-256; paths must be absolute. Digests must be
independently established from reviewed artifacts, not computed from
arbitrary current files as a trust shortcut. This configuration is still
not executable authorization or current host attestation. Actual owner
enrollment and C# to Node process integration remain separate validation
steps.


### Real subprocess transport probe

The shared internal C# planning transport now requests process-tree
termination on timeout, caller cancellation or pipe overflow while
preserving the distinct failure reason. A local opt-in smoke probe uses
an explicitly selected Node executable and trusted fixtures to exercise
actual UTF-8 JSON pipes, malformed responses, output limits, timeout,
caller cancellation and the real binder's missing-provenance rejection.
See `docs/LOCAL_VALIDATION.md`. This tests transport, not actual
owner-enrolled project admission or runtime qualification.

### Exact behavioral case coverage across the bridge

The C# planning binder validates the selected fixed contract and exact
current approved criterion text before starting Node. It checks that the
returned mapping is identical after ordinal normalization. The durable
ledger independently refuses unknown, duplicate, missing, empty or extra
behavioral cases and mismatched criterion IDs/text. Its structural
coverage fixture is not a reviewed semantic mapping for the timer task.
The seven existing Calculation Journal cases are unchanged, and there is
no default contract for unrelated projects. Human semantic review and
owner-local admission remain necessary; no execution authority is added.

### Durable project behavioral review

The internal review ledger records an exact semantic mapping bound to the
task, approved plan/specification/architecture, evaluator SHA-256 and
normalized mandatory-case coverage. An explicit `REVIEW BEHAVIOR <digest>`
acknowledgement is separate from exact candidate planning acknowledgement
and never grants execution. The binder retrieves the persisted reviewed
mapping rather than accepting a raw contract from its caller. Candidate
admission staging rechecks the review and evaluator digest transactionally,
links the two records, and rejects revocation and replay. Prior planning
records without a link remain historical. The review can be reused across
source-only repairs with unchanged approved requirements and evaluator;
changed requirements require a new review. Interrupted and revoked exact
reviews cannot silently reactivate. No HTTP/model route or automatic
semantic approval is added. Tests use synthetic coverage only.
