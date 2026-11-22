# M4.4 — Independent Calculation Journal trusted-reference evaluator (Windows)

**Status: implementation candidate, pending actual Windows Docker Desktop run.**
This is a real browser evaluator for two **frozen, checked-in, trusted**
calculator fixtures, *not an executor for user or model-generated code*.
The previous M4.2 (18/18) and M4.3 (17/17 guest, 9/9 host) completed real
Windows acceptance, with containers and disposable worktrees removed.

## What is new

The M4.3 smoke test only loaded a small hardcoded HTML page. M4.4
moves to complete cross-file calculator fixtures already covered by
the real Playwright regression suite:
- The frozen broken #107 negative fixture, with an unlinked JavaScript
  file and visually rendered but inert buttons.
- The separate hand-authored working reference, including keyboard
  operations, decimal precision, divide by zero, history and persistence.

The **host** loads only four hardcoded, Git-blob-hash pinned and reviewed
files from the checked-in regression directory. It does not take any
task ID, path, zip, user input, model response or arbitrary source.
It writes exact byte-for-byte copies into a new OS-managed temporary
snapshot, records SHA-256 values, creates a neighbor sentinel, and
bind-mounts the temporary snapshot into the Linux guest at /workspace
**read-only**. The original repo and personal files are never mounted.

The one fixed guest source is streamed through stdin. The browser
acceptance harness is also checked-in, independently reviewed and
embedded into that exact fixed guest by the trusted host, never read
from the evaluated web app. Inside the guest, a temporary loopback-only
HTTP server serves only the four allowlisted files and two trusted
test harness pages. Docker is still --network=none with no published
ports, read-only root, non-root UID, drop-all capabilities, no-new-
privileges, finite CPU/memory/PID/time and a temporary browser profile.

Chromium Headless Shell uses --no-sandbox **only for these fixed,
trusted files**, not for generated code. The evaluator checks 16
independent Boolean outcomes, including:

- Working JS syntax, linked scripts, and the broken app's unlinked JS
- Browser negative control: broken 2 and equals buttons stay inert
- Working mouse 2 + 3 = 5
- Keyboard 12.5 / 2 = 6.25
- Decimal 0.1 + 0.2 = 0.3
- Backspace and divide-by-zero behavior
- Back recall and History selection
- Stored history after page reload and after a **new Chromium process**
- Read-only source mount, neighbor unmounted, child system-write denial,
  outbound TCP denial and nonroot execution

The host independently audits **4 facts**: all snapshot files remain
exactly their original pinned bytes, adjacent host sentinel unchanged,
original regression source files unchanged, and no Git metadata in the
snapshot. The host confirms the exact randomly named container was
removed and deletes/verifies removal of the temporary snapshot.
Missing results, a failing negative control, a failing positive case,
a changed file or uncertain cleanup cause BLOCKED. Raw browser logs
are not saved, only bounded Boolean cases and hashes.

No build/package from user source is created. A positive result is
TRUSTED_REFERENCE_EVALUATOR_PASS, **not VERIFIED** for a CodingAssistant
generated artifact and **not static-web QUALIFIED**.

## Windows owner acceptance

From the repository root, Docker Desktop running in Linux mode:

~~~powershell
git switch main
git pull --ff-only
node tools/m4/windows_calculator_evaluator_qualification.mjs --run
~~~

This command uses the already-cached official
mcr.microsoft.com/playwright:v1.65.1-noble-amd64 image and never pulls.
Calling the script without --run prints NOT_RUN without calling Docker.

A real success would look like:

~~~text
M4.4 WINDOWS_TRUSTED_REFERENCE_EVALUATOR_PASS
  independent evaluator cases: 16/16
  host snapshot integrity: 4/4
  container removed: true; snapshot removed: true
  generated source / untrusted browser: NOT_AUTHORIZED
  Static Web runtime: UNQUALIFIED
~~~

The output also prints the local evidence path under
%LOCALAPPDATA%\CodingAssistant\qualifications.

## Scope and remaining release gates

A complete qualified generated-code evaluator still requires a separately
reviewed browser security profile for **untrusted** JavaScript and HTML.
This trusted fixture uses --no-sandbox only for frozen local source,
does not test arbitrary user or model source, and does not complete
#76's ten integrated adversarial cases, cancellation/recovery or
#92's task/plan/image/source-digest admission. #111 must eventually
connect real approved task-specific generated source, independent browser
tests, truthful VERIFIED/TESTS_FAILED status and bounded repair.

Model-free Windows/Ubuntu CI tests the source pinning, Docker command
policy, real local Node syntax and mocked failure/audit paths.
The existing CI Playwright suite runs the trusted browser fixtures
on both platforms, but CI does **not** run this new Docker Desktop guest.
The real Windows host outcome must be reported separately.
