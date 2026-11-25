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
