# AET-11 — Owner-host qualification runbook (not a pass)

The AET-0–AET-11 architectural baseline remains unchanged. The branch
`feature/autonomous-engineering-team` is isolated; do not merge into main.
Model-free regression evidence is necessary but cannot qualify live local
models or arbitrary generated source.

## 1. Establish the exact local revision

From a clean existing Windows checkout, after the exact branch head is green:

~~~powershell
git status --short
git fetch origin
git switch feature/autonomous-engineering-team
git pull --ff-only
git rev-parse HEAD
ollama list
~~~

If the local branch does not yet exist, use
`git switch --track origin/feature/autonomous-engineering-team`.
Do not discard a dirty checkout. Record the commit and actual local model
names/digests; model selection stays with the owner. Keep the shared API
inference capacity at its default of one for the first live scenario.
Start the normal Windows launcher or Visual Studio F5, without enabling
the M1 execution bridge. Reuse the existing localhost session mechanism.

## 2. Fixed compatibility demonstrated; runtime qualification still required

On November 24, 2026 the owner reported M4.7 11/11 at `9d78e08`,
full Chromium DOM PASS, internal sandbox status PASS, USER_NAMESPACE_OK,
confirmed container cleanup, fixture-only chroot policy and PID ceiling 256.
The private evidence JSON remains on the owner host. This is reported
owner evidence, not an independently retrieved artifact. No further repeat
of the unchanged fixed probe is required absent relevant configuration or
revision changes. For requalification when needed, the command is:

~~~powershell
node tools/m4/windows_chromium_sandbox_qualification.mjs --run
~~~

It uses the cached pinned image and a reviewed fixed guest only. Keep all
sandbox, capability, namespace, network, mount, and cleanup checks intact.
Do not add privilege, disable browser sandboxing, mount host credentials,
or execute generated code to make the result green. A new BLOCKED result
is a genuine blocker; retain the private report and its compact failure
categories. Even 11/11 compatibility is NOT qualification of untrusted
generated code. The separately approved, adversarially qualified
task-specific evaluator and exact source-bound independent functional
tests are still required. No planning registry flag or static/smoke result
may promote the runtime or product to verified.

## 3. Real-model collaboration scenario (source-only until qualified)

Create a fresh disposable project for an offline calculation journal.
Require mouse and keyboard operation, decimal arithmetic, divide-by-zero
recovery, and history retained after page reload and browser restart.
Approve the exact specification and the explicit static HTML/CSS/JS
architecture if those recommendations meet the requirements. Review and
approve the exact implementation plan. Assign installed models per role
and inspect the displayed model choices.

Observe six distinct roles, real provider requests and host-backed events.
Authorize only the routine delegation policy for the exact plan; repair
and any executable scope must remain separately authorized. Start source
generation, inspect Code Atlas and saved work, and start coordination.
Observe independent Lead Software Engineer review before QA, genuine
findings and exact source fingerprints. A deliberately defective case
must be separately reviewed as a trusted negative fixture or actual
observed source defect; never inject a finding or edit a model-produced
ledger merely to force success. If a defect is confirmed, authorize
only its exact finding and file scope; observe the new source revision,
originating reviewer recheck and further independent QA when applicable.
Exercise more than three distinct repairs only when genuine defects
justify them; repeated ineffective strategies must escalate, not loop
forever. Do not report a test the model merely says it ran.

Use the activity view and inspect delivery readiness. Source-only export
is inspection, not delivery. Until qualified verification and Product
Owner acceptance exist, delivery must remain blocked even if the UI shows
source complete, static reviews complete or a smoke pass.

## 4. Required acceptance matrix

Retain evidence for each item; mark Not run, Blocked, Failed or Passed.
Never convert a model-free fixture pass to live qualification.

| Area | Evidence required |
|---|---|
| Independent engineering review | Actual model detects a known architecture defect; no unauthorized source changes |
| Adaptive planning | Actual work-package dependencies and scoped dispatch; mandatory objectives retained |
| Repair | Exact owner-admitted repair; changed source; originating reviewer recheck; QA repair and qualified regression |
| Escalation | Observed repeated ineffective strategy or genuine unresolved conflict; no fixed three-cycle ceiling |
| Approval invalidation | Changed plan, specification, architecture, source or delegation blocks stale actions |
| Role authority | Unauthorized writes/execution denied; malformed output stops safely without writes |
| Recovery | Interrupt an actual job; durable receipts survive restart; no silent replay; exact approved resume |
| Capacity | Actual queue and active requests respect configured capacity; cancellation releases lease |
| Engineering Floor | Real events, handoffs and recorded model identities; presentation pause does not stop work |
| Code Atlas | Persisted source graph and revision changes survive reload |
| Verification | Independently qualified runtime and exact source functional evidence; mandatory gates cannot be bypassed |
| Product acceptance | Product Owner disposition tied to accepted current evidence; delivery blocked without it |
| Retrospective | Actual requirements, work, decisions, findings, repair lineage, verification, limitations, resource receipts |
| Regression/migration | Green Ubuntu and Windows model-free and browser tests; old data retained; no main merge |

## Current disposition

AET-9 model-free regressions were green at `54c9792`. AET-10 delivery
evidence foundation and UI were green on Windows and Ubuntu at `28a408d`
(run 45464643358). M4.7 fixed compatibility is owner-reported PASS at
`9d78e08`. The final positive acceptance/delivery path is not complete.
AET-8's executable qualification and AET-11 live acceptance remain
unqualified. The next executable work must satisfy the separately approved
scope and integrated adversarial gates (#76, #92, #111); no runtime promotion
or delivery claim follows from fixed compatibility.


## Trusted integrated evaluator — next owner-host command

After the exact branch revision passes CI, run `node tools/m4/windows_aet8_trusted_qualification.mjs --run`. The expected positive report is ten security checks, five integrated fixture checks, working 7/7, broken 0/7, spoof 0/7, container removed true. This is not another standalone compatibility probe: it tests independent functional observations and negative controls under the same approved trusted guest policy. Retain the private JSON. Generated execution is not authorized by this run.

## November 27 trusted integrated result

The owner has now reported the expected Windows trusted integrated PASS:
10/10 security, 5/5 fixture checks, working 7/7, broken and spoof 0/7
(each seven cases completed), COMPLETE and container removed true.
Private report: `windows-static-web-653ce5730b28.json`. No repeat of
unchanged trusted fixture is needed just to establish this milestone.
Raw report and exact digests remain on the owner host. Generated-source
execution and the Static Web runtime registry remain NOT_AUTHORIZED /
UNQUALIFIED. This supersedes the earlier pending trusted Windows fixture
checkpoint only, not the separate executable-admission and acceptance gates.


## November 27 candidate-shaped adapter result and next policy gate

At `575efd5` the owner reported 36/36 model-free JavaScript regressions
and the real Windows Docker `FIXED_CANDIDATE_ADAPTER_PASS`. Working
7/7; broken 0/7; spoof 0/7; all 21 cases complete; all three containers
removed. The owner-local receipt is
`windows-static-web-15a56d32786a.json`. This is reported evidence,
not independently retrieved or attested evidence. Do not repeat the
unchanged fixture qualification unless a relevant binding changes.

### Proposed next increment — separate generated-source qualification

This is a proposal requiring owner review, not a policy activation.
The current default-deny derived seccomp policy (including the fixed
fixture chroot exception) and PID ceiling of 256 were approved for
trusted fixtures only. Extending that policy to untrusted HTML/CSS/JS
is a material security decision. Existing owner source-only repair
delegation is not consent to this extension.

Proposed scope:

- A separate opt-in local Windows Docker qualification/adapter for the
  Calculation Journal contract only, exact approved source and manifest,
  bounded HTML/CSS/JS/JSON asset payload; no general shell or task runtime.
- Retain the pinned image and full Chromium renderer sandbox, private
  debugging pipe, default-deny seccomp with no additional syscall
  exceptions beyond the existing fixture profile, PID ceiling at most
  256, existing memory/CPU/time limits, nonroot, no new privileges, no
  capabilities, read-only root, private IPC, bounded ephemeral scratch,
  no outbound network, host mounts, repo/home, credentials, Docker
  socket, package installation or arbitrary host commands.
- Owner-reviewed candidate-specific adversarial qualification with exact
  host/image/adapter/evaluator/policy identities and positive/negative
  security evidence, not a copy of the fixed fixture receipt. Include
  independent verification of process and container cleanup, cancellation,
  interruption, recovery, replay/revocation and a fail-closed uncertain
  cleanup state. Preserve #76/#92/#111 gates. No registry promotion
  before successful required qualification.
- Only after separate owner approval of the exact executable policy and
  actual qualification: exact task/source/plan/contract-bound execution
  consent, transactional single-use admission, independent results
  and at most two scoped runtime repair/recheck attempts as in #111.
  No blanket generated-code execution or deployment. Any change in
  approved source, evaluator, policy or scope must be re-bound; existing
  source-only delegation must never silently become executable consent.
- First live project is a disposable Calculation Journal; Idea Vault
  remains preserved and requires its own behavioral contract and
  applicable reviewed verification scope. No main merge.

The next owner decision is whether to permit implementation and
qualification of this narrow proposed policy for candidate source,
including evaluating the previously fixture-only seccomp/PID parameters
for that scope. This is not an authorization to launch generated source.
Any need for additional privileges or weakened isolation must return
to the owner. Until approval and required qualification, generated
execution remains blocked. Source-only development remains permitted.


### Owner approval — implementation and qualification only

On November 27, 2026 the owner explicitly approved implementation and
qualification of the proposed narrowly scoped generated-source evaluation
adapter, retaining all stated security boundaries, and explicitly
prohibited activation before required qualification and separate
executable authorization. This supersedes the pending implementation
decision above, not the separate runtime qualification or consent gate.

The new pilot policy module constructs a distinct fixed qualification
profile and audits the actual Docker create-time configuration before
any future start. It retains the approved isolation options, adds a
no-swap allowance (memory-swap equals the memory ceiling), and deliberately
uses explicit create/inspect/cleanup rather than auto-removal. This is
a stronger resource restriction and a lifecycle design choice, not a
new permission. Model-free tests cover configuration drift. The module
does not call Docker or permit execution. Host create/start/cancel/recovery
integration and real adversarial qualification remain required.


The next real host check is the stopped-container configuration probe
in the AET-8 evidence document. It does not start a browser or repeat
the trusted candidate fixture run. It blocks if any prior pilot
qualification container is still present, leaving recovery to an
explicit reconciler rather than guessing ownership.


The owner reported the stopped-container configuration probe PASS at
`e1823cd` (private receipt `windows-static-web-871073428168.json`).
The next fixed-control lifecycle probe records its attempt under
`%LOCALAPPDATA%/CodingAssistant/pilot-attempts`. It starts only the
frozen working reference under the separate audited policy. Explicit
recovery is available for an interrupted, exact owned attempt; it
does not restart the guest. A live owner or ambiguous create outcome
blocks recovery. No generated execution or runtime registry promotion
follows from these checks.
