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

### Exact execution authorization proposal (not a grant)

The internal execution authorization ledger reconstructs a reviewed current
candidate, checks its descriptor and linked reviewed behavioral contract,
and records a digest binding task/run, source/scope/archive, exact plan
review, evaluator and historical fixture receipt, and the current
static-web registry profile. Proposed records are durable, revocable and
interrupted on restart; identical replay cannot reactivate them. Changed
or cancelled candidate planning evidence makes a proposal non-current.
Planning confirmation is not execution confirmation. Even a matching
execution confirmation is refused while the runtime is unqualified.
The schema deliberately has no Authorized or Consumed state yet and
there is no worker, public route, runtime grant or generated execution.
Current host/adapter qualification, exact executable policy binding,
single-use transactional consumption and cleanup remain required. Do not
ask the owner for executable consent at this stage.


### Candidate-shaped staging and observation contract

The separate candidate payload builder recomputes the exact current
admission and creates a bounded JSON asset payload. Its pure in-memory
asset resolver independently rechecks every file hash, size, manifest
order, source fingerprint and admission binding; only exact GET paths
from the manifest are served (no normalization, host filesystem fallback
or dynamic route). It starts no server or process.

The candidate observation parser requires the host's exact attempt,
candidate, archive, evaluator, adapter and policy identities and complete
known security/build/driver/case keys. It distinguishes functional failure
from incomplete driver observations and blocks contradictory results.
Even all true yields CHECKS_PASSED_NOT_VERIFIED. Neither parser nor
payload builder qualifies a runtime, authenticates a guest, grants
execution or proves process cleanup. The trusted fixed fixture runner,
its hashes and policy are unchanged. Before any generated source can
execute, a real independent candidate adapter must be qualified and its
exact executable policy separately authorized.


### Candidate browser driver (unqualified)

The separate candidate driver uses a fixed loopback /index.html endpoint,
private Chromium pipe transport, a restricted calculator locator/key
vocabulary and CDP isolated-world DOM reads, rather than running test
expressions against the candidate's modified JavaScript builtins.
Every case receives a fresh profile and browser process. Cold restart
requires a clean previous exit and preserves only that case's profile.
Stale page handles and concurrent sessions fail closed. A failed shutdown
blocks further cases; the container lifecycle must establish cleanup.
Model-free tests exercise the lifecycle and CDP command shape with
injected operations, not a real browser or generated application. The
real candidate driver is not connected to a CLI, host dispatcher or
authorization grant. The existing trusted fixture driver and policy
remain unchanged. Isolated-world observation is not proof of renderer
security, browser sandbox qualification or independent guest integrity.


### Composed candidate guest lifecycle (unqualified)

The candidate-specific lifecycle now composes exact in-memory source
binding, trusted security/build/browser-sandbox probes, a bounded
loopback asset server, the independent browser driver, and the existing
seven-case Calculation Journal contract. Missing UI controls can be
functional failures; transport, launch, navigation and cleanup failures
block verification. Browser and server teardown are attempted separately.
Source integrity mismatch is an infrastructure/security blocker, not
a repairable build failure. The result contract now explicitly requires
asset server closure and preserves incomplete security observations as
null rather than fabricating failed checks.

Model-free lifecycle tests inject trusted operations and an inert page
into the real behavioral contract. The HTTP regression uses only frozen
test bytes on loopback and does not launch a browser. Real guest security,
build and sandbox probes still require pinned integration and qualification;
the composed lifecycle is not called by any host dispatcher or CLI.
No new policy, execution grant or qualified runtime is established.


### Fixed candidate-shaped adapter qualification (owner-local)

The new fixed qualification command exercises the composed candidate
adapter in separate disposable containers with the already-reviewed
working calculator, inert broken HTML, and a frozen spoofing control
(including a page-level DOM method override). The guest bundle includes
only the explicit trusted module closure, uses the unchanged fixture-only
Docker isolation options with a separate fixed guest entry flag, and reports exact adapter/evaluator/policy identities.
Its synthetic fixture identities are not project approvals or ZIP
admissions. The runner refuses arbitrary source input, remote Docker
contexts, missing observations, unexpected negatives and uncertain
cleanup. The previous qualified fixture files and runner are unchanged.

After model-free tests pass, the owner may run:

~~~powershell
node tools/m4/windows_candidate_fixture_qualification.mjs --run
~~~

Expected FIXED_CANDIDATE_ADAPTER_PASS, working 7/7, broken and spoof 0/7
(all cases completed), every container removed. This is a new trusted
fixture compatibility check, NOT qualification for generated source.
No policy expansion, executable grant, or runtime registry promotion.


### Owner-reported real candidate-shaped adapter PASS

On November 27, 2026 at `575efd5`, the owner reported 36/36 JavaScript
regressions and `AET-8 WINDOWS_FIXED_CANDIDATE_ADAPTER_PASS`.
Actual Chromium observations: working 7/7, broken 0/7, spoof 0/7;
all 21 cases completed, all stages COMPLETE, all three containers removed.
Private owner-local evidence: `windows-static-web-15a56d32786a.json`.
This is owner-reported evidence; the raw receipt and digests have not
been independently retrieved here. The unchanged fixed candidate
adapter need not be re-run merely to demonstrate the same compatibility.

Generated source remains NOT_AUTHORIZED and Static Web UNQUALIFIED.
The prior fixture-only seccomp and PID-256 exception cannot be inherited
as executable generated-source authority. The current exact execution
proposal ledger does not grant execution. See the next policy decision
in `docs/AET_11_OWNER_HOST_QUALIFICATION.md`.


### Separate pilot policy construction and pre-start audit

Owner implementation/qualification approval is recorded in the AET-11
runbook. The separate pilot policy pins adapter, evaluator, exact image
identity, seccomp, environment hash, contract and resource limits.
It emits only a fixed create command, and audits the returned immutable
container ID, ownership labels, stopped state, actual HostConfig,
entrypoint, user, environment, mount/namespace restrictions, resource
ceilings and exact seccomp JSON. Unexpected configuration fails closed.
It deliberately adds no-swap and disables auto-removal to enable exact
container reconciliation. No runner, host admission, generated execution
grant or runtime promotion is added. The previously qualified fixture
adapter and fixture policy are unchanged.


The owner-local configuration qualification command creates only a
stopped container, audits its real configuration, removes it by exact
immutable ID, and independently confirms absence. It never calls start,
run or exec. Wrong ownership, drift, prior cleanup debt or uncertain
removal blocks the probe. It cannot reconcile a previously abandoned
attempt automatically; this is deliberately deferred to the durable
recovery lifecycle, and no guest may start through this command.

~~~powershell
node tools/m4/windows_pilot_configuration_qualification.mjs --run
~~~

Expected `AET-8 WINDOWS_PILOT_CONTAINER_CONFIGURATION_PASS`, actual
configuration audited true, container removed true, guest started false.
This is a distinct real host policy audit, not generated-source
qualification or consent.


### Owner-reported stopped-container PASS and durable pilot lifecycle

At `e1823cd` the owner reported
`AET-8 WINDOWS_PILOT_CONTAINER_CONFIGURATION_PASS`, configuration
audited true, removed true, guest started false. Local evidence file
`windows-static-web-871073428168.json` is not independently retrieved.
The conditional regression command reached the configuration probe.

The new qualification-only lifecycle reserves a durable exclusive
owner-local journal before container creation. It records the exact
attempt, candidate/archive, policy/image and Docker engine identity,
then creates, audits, starts **only the frozen working control**, checks
actual guest exit and independently removes the exact owned container.
It archives the append-only fsynced journal only after absence checks.
A crash retains the active journal, rejects new attempts, and requires
explicit recovery of the same identity. Recovery never resumes or
replays the guest. Unknown create outcomes, damaged journals, live
owner PIDs and interrupted recovery remain blocked for owner
reconciliation. The CLI is a trusted owner-local diagnostic, not OS
attestation, a hardened service lock or authorization. In particular,
local same-account tampering and simultaneous Docker administrative
actions are outside this diagnostic's trust boundary.

Run model-free tests, then the new fixed-control lifecycle qualification:

~~~powershell
node tools/m4/windows_pilot_lifecycle_qualification.mjs --run
~~~

Expected `AET-8 WINDOWS_PILOT_DURABLE_FIXED_CONTROL_PASS`, working
7/7, container removed true, cleanup debt false. This exercises the
actual pilot configuration, not the old fixture-only run policy.
No project archive or generated source is accepted. Actual
cancellation/crash adversarial runs, full negative control matrix,
pinned control-plane integration and executable authorization remain
required. Do not run recovery against an active owner process or
manually delete unresolved journals.


### Owner-reported durable fixed-control PASS and negative/interruption controls

At `92a37db` the owner reported
`AET-8 WINDOWS_PILOT_DURABLE_FIXED_CONTROL_PASS`, configuration audited
true, working 7/7 (all completed), container removed true, cleanup debt
false. Private receipt `windows-static-web-0fc9617b83a5.json`; raw
receipt not independently retrieved.

The same durable runner now accepts only the frozen working, broken and
spoof controls internally; the negative-only command requires both
negatives to complete all seven cases with zero passes, same engine and
policy identities, and confirmed cleanup. The separate interruption
supervisor launches the existing fixed working command as its own
child, observes the exact child-owned durable Starting journal and an
actual running Docker container, then exercises two distinct actions:
Docker cancellation by immutable container ID, and abrupt termination
of its own qualification owner process followed by explicit recovery.
Neither scenario is allowed to pass on successful guest exit, missing
running observation, missing archived cleanup, another owner's attempt,
or any unresolved cleanup debt. It uses only fixed trusted source;
no user-supplied archive, source path or program is accepted.

~~~powershell
node tools/m4/windows_pilot_lifecycle_qualification.mjs --run-negative-controls
node tools/m4/windows_pilot_interruption_qualification.mjs --run
~~~

The latter is a diagnostic supervisor, not production cancellation:
it confirms the owner process exit and container removal, not every
possible orphan host-side Docker client process. It does not qualify
arbitrary generated-source renderer isolation, establish host
attestation, grant execution or promote the runtime. Exact policy
and host evidence binding and remaining candidate security controls
are still required.


### Read-only binding of actual pilot qualification evidence

The pilot evidence inspector requires the existing durable working receipt,
negative-controls receipt and real interruption receipt. It reconstructs
the current fixed adapter and policy against the local Docker engine/image,
reclassifies every functional observation, and validates five distinct
archived attempt identities and complete journal transitions. Crash recovery
must have its recovery claim; cancellation and normal evaluation must not.
An active attempt, missing archive, changed source/control, image, policy,
engine, unexpected result or unresolved container blocks inspection.
No existing qualification command or qualified guest bytes change.

`node tools/m4/static_web_pilot_evidence.mjs --inspect WORKING_RECEIPT NEGATIVE_RECEIPT INTERRUPTION_RECEIPT`
takes only owner-local receipt basenames. It returns
`PILOT_HISTORICAL_EVIDENCE_BOUND` only when all records match; it cannot
establish that local files were not fabricated, bind historical host
executables, establish current host attestation, or grant runtime
qualification. Actual candidate-specific security qualification and
separate executable authorization remain mandatory. The negative and
interruption runs are still pending owner evidence.


### Owner-reported negative and interruption PASS; renderer boundary control

At `26522c1` the owner reported 56/56 regressions, durable negative
controls PASS (broken and spoof 0/7, all seven completed, removal true),
and actual interruption controls PASS (cancel and crash both observed
running, interrupted, owner exit confirmed, removed, cleanup debt false).
Private receipts: `windows-static-web-97d0c3738217.json` and
`windows-static-web-45135b58ac92.json`. No raw receipts independently
retrieved here. Do not repeat these unchanged runs merely for progress.

The new fixed browser-boundary control reuses the exact previously
exercised candidate driver, server, security and sandbox module bytes,
but has a distinct trusted probe entrypoint and adapter digest. It
checks the guest security/sandbox probes and a frozen page's observable
access to approved assets, denial of non-manifest and POST routes,
absence of Node globals and failed fetches to two fixed endpoints.
Network fetch failure is a sample, not proof of network confinement
(the actual Docker network policy audit remains mandatory). Its
trusted page result is read via the existing isolated-world driver.
It independently requires browser and server closure and uses the
same durable audited container lifecycle. This is not arbitrary
generated-source qualification or proof against browser exploits.

~~~powershell
node tools/m4/windows_pilot_lifecycle_qualification.mjs --run-browser-boundary
~~~

Expected `AET-8 WINDOWS_PILOT_BROWSER_BOUNDARY_PASS`, all five Boolean
categories true and confirmed removal. The distinct probe policy digest
does not replace or alter the original five-attempt evidence binding.
No generated-source execution grant, registry promotion or public
dispatch route is introduced.


### Owner-reported renderer boundary PASS and pinned control-plane inspection

At `633e5ea`, the owner reported 58/58 model-free regressions, all five
prior attempts bound (`b9f908d5fc114a617855f8f5341ca5205eeaf0c088b269eba70666ae8e17d898`),
and actual browser-boundary PASS with all five categories true, removal
true, cleanup debt false. Private receipt
`windows-static-web-17c3034f8bf2.json`; raw receipt not independently
retrieved. The six completed controls need not be repeated unchanged.

The inspector now optionally binds a fourth receipt and sixth archived
attempt, retaining the original five-attempt digest and distinct renderer
probe identity. Its bounded stdin operation uses the existing pinned
metadata Docker adapter. The new host-only .NET inspector pins all 34
module/data files plus the entrypoint and explicit Node/Docker executables,
checks them before and after inspection, and validates the response's
six distinct identities and non-executable flags. An owner-private
`evaluation-pilot-host.json` reader uses the existing strict config
schema but requires the separate exact repository-relative pin closure.
No automatic production pin enrollment, API route, or executable state.

The opt-in API smoke test hashes the current checked-out test files and
explicitly selected test executables; these are diagnostic pins only,
not production enrollment or historical provenance. It invokes the real
read-only .NET-to-Node inspector. Set CODINGASSISTANT_TEST_NODE_EXE,
CODINGASSISTANT_TEST_DOCKER_EXE and CODINGASSISTANT_TEST_PILOT_RECEIPTS
(four receipt basenames separated by semicolons). No container starts.
Expected `PILOT_PINNED_HOST_INSPECTION_PASS`.
Historical evidence remains non-executable; the generated candidate
transport, actual executable policy qualification and one-use owner
consent are still outstanding.


### Owner-reported control-plane smoke PASS and evidence-bound proposals

At `4a33849`, the owner reported M2 LOCAL_CONTROL_PLANE_SMOKE_PASS
(439 assertions). This follows the prior 58/58 regression and renderer
qualification. The smoke summary alone is not a separate attestation of
the host or historical executable identities.

The execution proposal ledger now supports a distinct host-only
evidence-bound proposal. It loads the owner-private pilot config, invokes
the pinned inspector, rechecks configuration, binds the exact six-attempt
inspection and current configuration hash, and transactionally checks
contract, evaluator, image, seccomp and PID ceiling against the exact
reviewed admission. The legacy planning-only proposal cannot stand in
for this binding. The proposal stores the immutable binding separately,
includes its digest in the authorization identity, and supports fresh
read-only reinspection and current-source revalidation. Get.Current
means database currentness only, not live host trust. Revocation and
startup interruption remain terminal; the registry and executable gate
stay disabled. The structural Phase 2 fixture is synthetic, not a
functional calculator or real executable grant.

No public dispatch or owner-consent bypass is introduced. Next work:
separate candidate transport/build restrictions and qualified executable
policy, followed by one-use consent and result persistence.


### Phase 2 proposal binding PASS and exact candidate transport assembly

At `4f6f85d` the owner reported PHASE 2 MODEL-FREE ACCEPTANCE: PASS
709 checks. The evidence-bound proposal ledger remains non-executable.
The next transport assembly module recomputes admission, candidate archive,
complete historical qualification, policy and adapter bindings from the
actual inputs. It rejects any mismatch or reuse of a qualification attempt
ID. The unchanged qualified guest template receives only bounded base64
JSON data; reverse substitution confirms exact template identity. The
result is a private in-memory preparation plus a digest/size descriptor,
never a grant, a command line or a user-facing artifact. No guest launches
and no files are extracted. There is no CLI or control-plane dispatcher
for this source yet. The inherited build probe is fixed-fixture-oriented;
candidate build scope and qualification of the actual executable policy
are still required before one-use execution authorization. Structural
fixtures in these tests are not functional calculator acceptance.


The private .NET transport planner captures the exact bound proposal,
current archive and stored reviewed contract transactionally. It invokes
only the pinned metadata-only transport preview bridge, selecting the
original trusted fixture receipt and the four stored pilot receipts.
It rechecks owner-private pin configuration, exact source and proposal
after the subprocess. The preview returns only hashes, counts and
lengths, never raw guest source or candidate bytes. The separate pilot
configuration pin closure now includes the three transport modules.
The previous guest adapter/policy and historical receipt identities
remain unchanged. No production host pin enrollment or execution
dispatch is introduced. The host-only planner's real integration is
not yet exercised against a live project and private configuration.


### Phase 2 transport PASS and separate restricted build adapter

At `6eb35f2` the owner reported Phase 2 PASS 712 checks (the preceding
conditional Node test gate reached Phase 2). The candidate transport
preview remains non-executable.

A new, deliberately narrow build adapter accepts one HTML entry
document with explicit balanced supported tags, quoted attributes,
local classic external JavaScript and linked local CSS (simple inline
style blocks are also supported), and optional parseable JSON data.
It rejects unsupported markup, inline scripts/handlers, missing and
unlinked script/style assets, ambiguous resource paths and invalid
classic script syntax. This is a conservative subset recognizer,
not an HTML standards parser or JavaScript safety analysis. Dynamic
behavior still requires the isolated browser and independent tests.
No candidate source is syntax-checked by a host process. The guest
runs a constant Node vm.Script parse-only subprocess in the existing
container limits, never running the compiled script. The invalid syntax
control uses a top-level return (accepted by CommonJS, invalid as a
browser classic script).
Unsupported markup/asset grammar is classified under the existing
local_assets_linked build Boolean; the original fixture evaluator and
historical evidence remain unchanged.

The new adapter has a distinct digest. Real qualification runs five
frozen controls under the unchanged isolation configuration:
working 7/7, spoof 0/7 with seven completed, and broken/missing asset/
invalid syntax each BUILD_FAILED with no browser observations.
The real runner is still fixed-source-only and no generated transport
uses the new adapter until the new evidence and consent are bound.

~~~powershell
node tools/m4/windows_pilot_lifecycle_qualification.mjs --run-build-controls
~~~

Expected `AET-8 WINDOWS_PILOT_BUILD_CONTROLS_PASS`, all five removed.
The new adapter needs this distinct qualification; it does not inherit
the prior fixture-only adapter PASS. No runtime promotion or grant.


### Owner-reported restricted build PASS and eleven-attempt evidence binding

At `8e3a82c` the owner reported AET-8 WINDOWS_PILOT_BUILD_CONTROLS_PASS.
The individual new receipt basename was not supplied. The previous
conditional model-free command reached the build qualifier if run as
documented. Do not repeat unchanged controls merely to obtain the name.

The inspector accepts a fifth receipt, validates the five distinct
new archived attempts and recomputes the observation classifications,
and retains both original evidence digests. The new policy must differ
only by the build adapter digest. The .NET inspector checks the exact
additional identities, policy invariants and eleven unique attempts.
The bound host proposal includes the complete upgraded evidence; the
host transport planner now requires the upgraded evidence and selects
the exact new adapter and policy. Older diagnostic previews remain
non-executable and cannot substitute for this new binding.

The existing opt-in .NET smoke accepts five receipt basenames separated
by semicolons in CODINGASSISTANT_TEST_PILOT_RECEIPTS and emits
PILOT_PINNED_BUILD_EVIDENCE_INSPECTION_PASS. No containers are started.
The real report is historical evidence, not runtime promotion,
retrospective host attestation or executable consent. Actual one-use
admission, pinned execution ownership and a distinct owner authorization
remain outstanding.


### Durable single-attempt admission

The owner reported M2 LOCAL_CONTROL_PLANE_SMOKE_PASS (439 assertions)
after the synthetic attempt-ID correction at `eb0d43a`. The preceding
conditional test gate reached the API smoke; the summary alone does
not identify the selected receipt count.

A separate SQLite admission row reserves one exact attempt per evidence-bound
proposal and makes attempt IDs unique across proposals. Consent identity
includes the entire transport descriptor, proposal and pilot binding.
Preparation/retrieval recheck current reviewed source, contract and
build-qualified evidence. Parent revocation and startup recovery
transactionally retire unconsumed admissions. Atomic conditional
Prepared -> Authorized -> Consumed transitions are implemented but remain
unreachable in production because the executable policy gate is closed.
No synthetic bypass, executable permission, public endpoint or dispatch.
The tests exercise rejection, identity binding, reservation replay,
revocation and recovery, not successful execution consumption. A future
qualified dispatcher must reproduce the exact attempt and fresh evidence
and coordinate cancellation/cleanup before the gate can be opened.


### Single-attempt ledger PASS and pinned dispatcher qualification

At `6b8d73d` the owner reported Phase 2 model-free acceptance PASS
723 checks. The new fixed-only dispatcher qualification requires explicit
Node and Docker binary SHA-256 pins, verifies those pins before and after
each Docker subprocess, and limits the subprocess boundary to fixed
metadata queries, exact audited policy create, exact ID attachment with
the reconstructed working build-qualified fixture bytes, and exact ID
removal. It validates the ordered guest stream, independent result,
durable archive, absence of cleanup debt and rejection of the same
attempt on replay. No arbitrary source or execution command is accepted.

Owner-local diagnostic pins identify the current binaries, not
retrospective attestation or production enrollment. The qualification
still needs actual Windows validation; the original build-qualified
guest and isolation policy bytes are unchanged. The real generated
dispatcher and its separately authorized consumption are not yet enabled.
Existing cancellation/crash receipts belong to the earlier fixed
adapter; they do not attest to this pinned dispatcher. No runtime
promotion, owner consent bypass, or changes to main.

Run with explicit local diagnostic binary pins:
~~~powershell
$env:CA_PILOT_NODE_SHA256 = (Get-FileHash (Get-Command node.exe).Source -Algorithm SHA256).Hash.ToLowerInvariant()
$env:CA_PILOT_DOCKER_EXE = (Get-Command docker.exe).Source
$env:CA_PILOT_DOCKER_SHA256 = (Get-FileHash $env:CA_PILOT_DOCKER_EXE -Algorithm SHA256).Hash.ToLowerInvariant()
node tools/m4/windows_pinned_dispatch_qualification.mjs --run-fixed-control
~~~
Expected AET-8 WINDOWS_PILOT_PINNED_DISPATCH_PASS, audited true,
removed true, replay blocked true, cleanup debt false.


### Owner-reported pinned dispatch PASS and actual pinned interruption controls

At `e71ba97` the owner reported AET-8 WINDOWS_PILOT_PINNED_DISPATCH_PASS,
audited/removal/replay true, no cleanup debt, private receipt
`windows-static-web-559e3505e708.json`. This is reported evidence,
not retrospective executable or module attestation.

The separate `--run-pinned` interruption mode supervises the exact
pinned fixed dispatcher with the build-qualified adapter. It verifies
the current repository module closure before and after each control,
requires the exact frozen fixture identity, policy and engine before
signaling, and binds the complete archived interruption sequence and
recovery claim. Both owner and Docker executable pins remain explicit.
The previous fixed controls and guest templates are unchanged.
Only a frozen reference is launched. Crash recovery confirms owner exit
and container removal, not Windows orphan Docker client process exit;
same-account races and production admission integration remain outside
this qualification. No execution gate is opened.

~~~powershell
node tools/m4/windows_pilot_interruption_qualification.mjs --run-pinned
~~~

Expected AET-8 WINDOWS_PILOT_PINNED_INTERRUPTION_CONTROLS_PASS, both
controls running observed/interrupted/owner exit/removal true, no debt.
Use the same CA_PILOT_NODE_SHA256, CA_PILOT_DOCKER_EXE and
CA_PILOT_DOCKER_SHA256 values as the pinned dispatcher control.


The separate read-only `--inspect-pinned-dispatch` command accepts
exactly seven receipt names in order (working, negatives, original
interruptions, renderer boundary, build, pinned dispatcher, pinned
interruptions). It validates all fourteen distinct archives and current
binary/module identities and recomputes the positive observation and
transport hashes. It preserves earlier evidence digests. This command
is diagnostic only; the .NET proposal inspector still accepts four or
five receipt names. No historical module provenance is inferred for
the earlier positive dispatcher run. The new closure excludes planning
and evidence-only entrypoints so their changes do not invalidate the
pinned interruption control.


### Owner-reported pinned interruption PASS and complete admission binding

At `5b568bd` the owner reported both actual pinned interruption controls
running/interrupted/owner exit/removal true, cleanup debt false,
AET-8 WINDOWS_PILOT_PINNED_INTERRUPTION_CONTROLS_PASS, receipt
`windows-static-web-d3d7e9c05727.json`. The positive pinned dispatch
receipt is `windows-static-web-559e3505e708.json`.

The .NET inspector now accepts the exact seven-receipt selection,
validates all fourteen distinct attempt identities, requires the
dispatcher policy, adapter and current executable pins to match,
and uses the bounded metadata-only stdin operation. The proposal,
transport and single-attempt admission ledger bind the full dispatch
evidence digest. Earlier build-only proposals are diagnostic and
cannot reserve candidate attempts. No launch-dependent module changes
are made, preserving the pinned interruption closure. The opt-in API
smoke emits PILOT_PINNED_DISPATCH_EVIDENCE_INSPECTION_PASS when the
seven-receipt inspection succeeds.

This does not qualify a generated-source dispatcher or prove orphaned
host Docker client termination. The next implementation must address
actual generated dispatch ownership/termination and result persistence
under exact consumed authorization; the runtime gate remains closed.


### Complete pinned evidence PASS and shared candidate dispatch lifecycle

At `c30cce2` the owner reported Phase 2 PASS 725 checks. The documented
conditional command reached Phase 2 after Node and API smoke. The
summary does not independently prove the optional inspection marker.

The new static-web candidate dispatcher reconstructs the reviewed
transport against the exact reserved descriptor and fourteen-attempt
evidence. Its generated-source entry point remains hard closed. The
new private shared lifecycle is exercised exclusively by frozen
qualification controls. It creates and audits a stopped exact-ID
container, uses an asynchronous pinned Docker attach child with
bounded output and deadline, observes the actual close event, checks
engine and container exit, classifies the ordered independent result,
and exposes observations only after confirmed removal and archive.
An unconfirmed attach close or unknown create outcome leaves durable
cleanup debt and no functional verdict. Build failure, test failure,
and complete checks are distinct; no VERIFIED state is emitted.

The earlier pinned dispatcher and interruption closure are not changed.
The new lifecycle is not qualified by those receipts alone. The
current implementation confirms attach close during normal operation
and requested interruption, but does not yet put the Docker child
under a Windows kill-on-close job for abrupt owner loss. Production
cancellation, durable .NET result admission and executable owner
authorization remain outstanding. No generated dispatch is possible.

~~~powershell
node tools/m4/static_web_candidate_dispatch.mjs --run-fixed-controls
~~~

Expected AET-8 WINDOWS_PILOT_CANDIDATE_DISPATCH_CONTROLS_PASS:
working CHECKS_PASSED_NOT_VERIFIED, spoof TESTS_FAILED, missing asset
BUILD_FAILED, all attach closed and removed, no debt.
Use the previously selected CA_PILOT_* executable pins.


### Shared lifecycle PASS and durable result ledger

At `1fc8488` the owner reported AET-8 WINDOWS_PILOT_CANDIDATE_DISPATCH_CONTROLS_PASS,
working CHECKS_PASSED_NOT_VERIFIED, spoof TESTS_FAILED, missing asset
BUILD_FAILED, all attach closed, removed, no cleanup debt. Receipt
`windows-static-web-3b7cd4d3e0b1.json`.

The private .NET admission ledger now supports exactly one immutable
result per consumed attempt. It binds the original transport and
attempt, recomputes the seven-case classification from exact Boolean
observations, and rejects success without complete security, build,
browser and cleanup evidence. Exact receipt replay is idempotent;
changed receipts are rejected. A revoked or interrupted consumed attempt
without a result receives a terminal blocked record with no fabricated
guest observations or cleanup claim. Historical results survive recovery
but cannot be current once their proposal is retired.

These tests use synthetic consumed rows in a disposable smoke database;
there is no production authorization bypass or public result route.
The result parser validates structure, not subprocess provenance, and
does not grant VERIFIED or trigger repairs. Binding the new lifecycle
receipt and qualifying production Windows process-tree ownership remain
outstanding. The executable gate is still closed.


### Result ledger PASS and Windows job ownership

At `b7a233e` the owner reported Phase 2 model-free acceptance PASS
736 checks. The next ownership primitive creates a non-inheritable
Windows Job Object with KILL_ON_JOB_CLOSE, no breakaway, and uses
STARTUPINFOEX JOB_LIST to assign the fixed Node process at creation.
There is no start-then-assign window. The job contains the inherited
Docker child tree. The fixed-control qualification runs the actual
shared dispatcher normally, then observes the actual Node and Docker
processes in a new job, retains their kernel process handles, closes
the observer job query handles, kills only the owner, and checks that
the sampled processes exit. Cleanup-only recovery requires the exact
build-working interrupted journal, pinned policy and engine, and
confirms removal and archival. The normal job must be empty at exit.

The .NET job launcher currently uses an explicit allowlist of fixed
control and recovery entrypoints, no generated source. It does not
yet provide redirected production stdin/stdout, a consumed grant or
proof against same-account interference. The diagnostic uses current
checkout hashes rather than production enrollment. Failed probes
must retain uncertain cleanup debt; do not infer cleanup from owner
termination alone. The older qualification closure is unchanged.

Run the opt-in Windows API smoke mode using the already selected
CA_PILOT_* executable pins and CODINGASSISTANT_TEST_NODE_EXE:
~~~powershell
dotnet run --project tests/CodingAssistant.Api.Smoke/CodingAssistant.Api.Smoke.csproj -- --pilot-job-ownership
~~~
Expected AET-8 WINDOWS_PILOT_JOB_OWNERSHIP_PASS. This is a new actual
process ownership control, not generated execution authorization.

The qualification also queries the pinned Docker engine for the exact
running container before killing the owner and records a private receipt
binding the actual normal and recovery receipt bytes, current executable
and module hashes, and observed process exit checks. This is sampled
process-handle evidence plus OS job semantics, not an exhaustive list of
all historical descendants or a generated execution grant.

### Job ownership observation correction

At `0a21c36` the owner reported the normal job-owned controls passed,
but the crash observer failed to capture live Node/Docker members.
This is not a crash qualification PASS. The new crash-only control
starts the same exact audited frozen working container via the shared
attach path but deliberately holds stdin open without sending the
guest program or EOF. It cannot finish successfully and is bounded
by the existing attach deadline. The observer checks actual live
members, the running owned container, journal identity, owner and
sampled descendant exits, and disappearance of the named job. The
normal three-control run still executes the full guest and is unchanged
in behavior. Additional observation diagnostics distinguish a missing
journal, process image, or live container. No generated input accepted.

Before retrying, check for retained active journal/labelled containers
from the failed observation. Do not remove unrelated containers or
manually delete the journal. If debt remains, report it before retrying;
prior unknown process ownership must not be treated as confirmed clean.


### Windows job ownership PASS and eighteen-attempt diagnostic binding

At `47e3313` the owner reported AET-8 WINDOWS_PILOT_JOB_OWNERSHIP_PASS.
The exact new receipt basename was not supplied. The new read-only
inspector accepts eight names (the previous seven plus the job
ownership receipt); it loads the two nested receipts using exact
basenames, verifies their raw byte hashes, rechecks all current module
pins and both binary pins, reconstructs the three frozen normal
controls and recovered crash archive, and excludes reuse across all
eighteen attempts. The .NET inspector validates the new schema and
distinct identities. API smoke emits PILOT_JOB_EVIDENCE_INSPECTION_PASS.

The job report's native assembly and old inspector hashes are historical
identity data, not an attestation of a newly rebuilt .NET executable.
The previously qualified launcher, guest, and module pin closure are
unchanged. The eight-receipt mode is diagnostic only; the production
proposal/transport continues to use the prior seven receipts until the
new native stdin/stdout dispatch and cancellation path is implemented
and independently qualified. No executable permission or VERIFIED
state is introduced. No unchanged Docker qualification rerun required.


### Job-owned fixed stdin/stdout transport

At `e2e5329` the owner reported M2 LOCAL_CONTROL_PLANE_SMOKE_PASS
439 assertions. The summary does not independently establish the
optional PILOT_JOB_EVIDENCE_INSPECTION_PASS marker.

A new fixed-only native I/O path uses the existing kill-on-close job
and atomic JOB_LIST assignment with a separate HANDLE_LIST containing
only the three child pipe ends. Parent ends are non-inheritable.
Bounded stdin, concurrent bounded output drains, deadline and
cancellation terminate and confirm the job; a failed I/O path
cannot publish a result or clear the container journal. Module,
script and executable pins are checked before and after the run.
The separate stdin bridge accepts only three frozen control names
and a fresh exact attempt ID. No candidate input or grant route.

The actual opt-in control exercises rejection, working, spoof,
missing asset, and pre-cancelled requests. New bridge identity is
pinned separately from the earlier 43-file historical closure.
No historical native binary qualification is transferred to the new
build. The actual running-candidate cancellation and crash recovery
through redirected I/O still require independent qualification.
The currently tested cancellation is pre-launch only. No production
dispatch, executable policy, or VERIFIED promotion is enabled.

~~~powershell
dotnet run --project tests/CodingAssistant.Api.Smoke/CodingAssistant.Api.Smoke.csproj -- --pilot-job-fixed-io
~~~

Expected AET-8 WINDOWS_PILOT_JOB_FIXED_IO_PASS, using existing
CA_PILOT_* and CODINGASSISTANT_TEST_NODE_EXE pins.

### Owner-reported fixed I/O PASS; redirected interruption qualification

At `89a9a44` the owner reported all 92 JavaScript regressions passing and
AET-8 WINDOWS_PILOT_JOB_FIXED_IO_PASS, private receipt
`windows-static-web-566202aab34a.json`. The new bridge accepts only an
additional exact one-field fixed-interruption request, reusing the frozen
held-stdin control without altering the historical dispatcher closure.
The actual qualification observes the exact running container and retained
Node/Docker process handles, then exercises native cancellation and abrupt
owner loss separately. Each requires job/process termination and exact
cleanup-only recovery before a pass. No generated input or grant route.
The changed bridge must pass normal fixed I/O again before the new control.

~~~powershell
node --test tests/test_m4_generated_zip_smoke.mjs
if ($LASTEXITCODE -eq 0) { dotnet run --project tests/CodingAssistant.Api.Smoke/CodingAssistant.Api.Smoke.csproj -- --pilot-job-fixed-io }
if ($LASTEXITCODE -eq 0) { dotnet run --project tests/CodingAssistant.Api.Smoke/CodingAssistant.Api.Smoke.csproj -- --pilot-job-io-interruptions }
~~~

Expected AET-8 WINDOWS_PILOT_JOB_IO_INTERRUPTION_PASS. Stop on a failed
control or active journal; never manually delete the journal. This is a
qualification-only increment, not evidence binding or executable consent.


### Owner-reported complete native I/O historical inspection PASS

On 2026-11-27, after PR #211 and the native I/O receipt schema correction
in PR #212 (main `ad98aa11cfc08fba618a36f80783f7b643ec2aca`), the owner
reported the following actual Windows host markers:

```text
Receipt count: 10
EVALUATION_PLANNING_NODE_TRANSPORT_PASS
PILOT_JOB_IO_EVIDENCE_INSPECTION_PASS
M2 LOCAL_CONTROL_PLANE_SMOKE_PASS (439 assertions)
```

The ten-receipt, 23-attempt historical inspection gate is complete as
owner-reported evidence. Original receipt bytes and hashes remain private
and unchanged. Do not rerun unchanged qualification controls solely to
repeat these markers. This is not current native executable attestation,
production Static Web qualification, generated-source execution consent,
or functional verification of any generated application. The existing
seven-receipt admission binding must still be upgraded to the complete
chain before executable admission can be considered; the executable gate
remains closed. No private receipt names or digests are inferred here.
