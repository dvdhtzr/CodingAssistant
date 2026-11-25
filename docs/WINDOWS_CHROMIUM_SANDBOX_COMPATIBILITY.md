# M4.7 — Windows Chromium sandbox-on compatibility qualification

**Status: pending Windows Docker Desktop owner-host acceptance.**
The owner reports M4.2 fixed guest 18/18, M4.3 scoped disposable
workspace 17/17 + independent host 9/9, and M4.4 trusted Calculation
Journal evaluator GREEN. M4.5 and M4.6 implement non-executable,
task/plan/source-bound verification scope and Mission Control display.

These earlier successes used Chromium's --no-sandbox flag only for
**reviewed and frozen** test fixtures. They do **not** establish that
untrusted HTML or JavaScript can safely run. M4.7 tests whether the
existing hardening profile can run Chromium with its own sandbox
enabled, before considering task-specific generated-code execution.

## What is being exercised

The host starts a single disposable Linux container using the existing
*cached and pinned* Microsoft Playwright image. All existing restrictions
remain: local Windows Docker Desktop Linux engine only, immutable image
ID and trusted registry digest, no image downloads/builds, network
disabled, read-only root, no host mounts/ports/secrets/sockets,
non-root 1000:1000, all capabilities dropped, no-new-privileges,
the **byte-pinned fixture-only derivative of the official Playwright seccomp profile** (default-deny
with its documented clone/setns/unshare namespace exceptions), finite
CPU/PID/memory/runtime limits, private IPC, disposable /tmp and
shared-memory limits.

One reviewed and SHA-256-recorded Node guest source is streamed over
stdin. It uses only a frozen local HTML file with a static DOM change
and Chromium's own internal chrome://sandbox status page. It does not
open the public web, download assets, accept task/model data, or
mount/generate an application workspace.

The Chromium command **does not contain** --no-sandbox,
--disable-setuid-sandbox, --single-process, --no-zygote,
--disable-seccomp-filter-sandbox, or other sandbox bypasses.
The guest verifies exactly 11 Boolean observations:

- Linux non-root identity and active seccomp filter
- Absent provider credentials, container sockets, and host workspaces
- Root filesystem and child-process system write rejection
- Parent and child outbound TCP denial
- Actual Chromium execution of the fixed DOM update, with sandbox ON
- Affirmative sandbox status from the internal Chromium status page

The host requires a strict versioned JSON response and allowlisted
browser diagnostics for the fixed DOM and sandbox-status probes.
Missing browser binary, namespace restrictions, denied sandbox startup,
timeout, crash, malformed output, no affirmative internal sandbox
status, unexpected process exit or any failed observation produce
**BLOCKED**, never an automatic downgrade to --no-sandbox.
Raw Chromium stderr, environment and host private paths are not
persisted or echoed.

The host checks container ownership before explicit cleanup and
verifies the container is absent; uncertain cleanup blocks success.
A green response is named **SANDBOX_COMPATIBILITY_PASS**. It remains
**not an untrusted Chromium security qualification**. It merely
establishes that Chrome can start and offers affirmative sandbox status
with these fixed inputs under this host's Docker configuration.

If this first conservative configuration blocks, investigate the
specific diagnostic before any further action. Playwright's guidance
for untrusted websites recommends a nonroot user **plus a reviewed
seccomp profile** which allows Chromium's required namespace syscalls;
never blindly use an unconfined seccomp or privileged container, share
host IPC, or disable browser sandboxing to force the check green.

Reference: https://playwright.dev/docs/docker

## Windows result and targeted resolution

The owner ran real Windows M4.7 and reported **9/11** with confirmed
container cleanup. Both remaining failures were
`CHROMIUM_SANDBOX_REJECTED` (fixed DOM and internal sandbox status).
The other nine checks passed. This is not browser acceptance PASS.

Playwright's **official recommended Docker seccomp profile** is now
vendored unchanged from
`microsoft/playwright/utils/docker/seccomp_profile.json` at upstream
Git blob `fddc05fb520affb145404e6f6f647ca96af8087d`.
The host verifies exact pinned bytes (allowing only LF/CRLF checkout
normalization), checks default-deny and the namespace allowance,
records SHA-256 evidence and supplies the checked file to Docker as
`--security-opt=seccomp=<checked local file path>`.

This is Playwright's documented mechanism for a non-root Chromium to
create namespaces. It does NOT use `seccomp=unconfined`, privileged
containers, extra Linux capabilities or browser sandbox bypass flags.
The scoped permissions are applied to the disposable container, with
all the other existing security limits still enforced.

The owner reuses the **same M4.7 command**, not another new milestone.
If the host kernel forbids namespace creation independently of the
seccomp profile, we retain BLOCKED rather than silently bypassing it.

After the compatibility blocker is resolved, prioritize actual
task-specific generated-source browser acceptance and bounded repair.
Do not add ceremonial fixed-fixture phases absent a concrete new
security finding. Complete #76/#92/#111 using integrated cases tied
to that actual workflow, with actionable application-specific evidence.

Reference: https://playwright.dev/docs/docker
## One command on Windows after GitHub CI green

From the existing CodingAssistant checkout, with Docker Desktop
running in Linux-container mode:

~~~powershell
git switch feature/autonomous-engineering-team
git pull --ff-only
node tools/m4/windows_chromium_sandbox_qualification.mjs --run
~~~

No-argument invocation prints NOT_RUN and never contacts Docker.
The already cached image is used; no model, Azure account, token, or
new image pull is required.

Expected success, *only if independently observed on the owner host*:

~~~text
M4.7 WINDOWS_SANDBOX_COMPATIBILITY_PASS
  fixed isolation and browser checks: 11/11
  container removed: true
  seccomp fixture policy: m47-fixed-chromium-chroot-v1
  fixed fixture PID limit: 256
  Chromium: FULL_CHROMIUM; fixed DOM: PASS; sandbox status: PASS
  generated source: NOT_AUTHORIZED; Static Web runtime: UNQUALIFIED
~~~

A legitimate BLOCKED result is useful evidence. The private report
under %LOCALAPPDATA%\CodingAssistant\qualifications contains the
exact failed check identifiers and bounded categories.

## Remaining gates before generated-source tests

1. Review and qualify an actual untrusted HTML/JS browser-execution
   profile and nested namespace/seccomp behavior, including a dedicated
   evaluator separated from generated source. This probe alone cannot
   prove all renderer-process protections or host escape prevention.
2. Bind an immutable, approved exact task, source digest, sandbox image
   and write allowlist to *separate* owner executable-scope consent (#92).
3. Complete issue #76 integrated ten adversarial cases including child,
   symlink/path, credential, network/Git, cancellation and crash recovery.
4. Independently run model-generated Calculation Journal behavior tests
   with truthful VERIFIED/TESTS_FAILED/VERIFICATION_BLOCKED evidence
   and a bounded max-2 repair loop (#111).

The M4.7 probe is not wired to any API/UI run action. No change is
made to RuntimeProfileRegistry: generated-code execution stays
disabled regardless of the fixed compatibility result.

CI only validates command policy, exact guest source and result schema,
browser flag omissions, simulated Docker/cleanup negatives and local
Node syntax. CI does **not** run this guest inside Docker Desktop.

## November 24 follow-up — full Chromium and stronger observations

Owner-host evidence again reports 9/11 with HEADLESS_SHELL and CHROMIUM_CRASH for both browser checks; cleanup is confirmed. The generic crash category did not establish the cause. The probe now prefers the cached full Chromium binary for its internal WebUI, creates separate private HOME/XDG/profile directories inside the same disposable tmpfs, and distinguishes allowlisted crash signals, Crashpad and profile errors. No Docker privileges, mounts, limits or seccomp policy change. The headless shell cannot establish a positive internal sandbox status.

The DOM check now requires the changed output element, not merely the marker in the script text. Script/style/template content is excluded from the internal status assertion. A positive browser Boolean with a conflicting diagnostic is rejected. The unchanged 11/11 requirement and cleanup gate still apply; this is a targeted compatibility/measurement correction, not a confirmed host fix or qualification. A subsequent BLOCKED report is actionable by its narrower category.

## SIGABRT follow-up

The owner confirmed both full-Chromium invocations abort with SIGABRT. The fixed guest now records bounded, allowlisted fatal-site categories, PID/memory cgroup event deltas and a fixed unprivileged user namespace probe. This distinguishes resource exhaustion from namespace/zygote failure without changing the sandbox, raising limits or exposing raw browser logs. Diagnostic observations are not additional pass criteria and cannot override any of the eleven checks. No generated source is executed.

## Owner-approved fixed-fixture chroot adjustment

On November 24, 2026 the owner-host result reported 9/11, FULL_CHROMIUM, SIGABRT, USER_NAMESPACE_OK and ZYGOTE_FATAL for both invocations. The upstream policy only allows chroot with CAP_SYS_CHROOT at the outer container level; Chromium may need the call within its own user namespace. The owner explicitly approved a single-system-call adjustment for the fixed fixture only. The separate m47-fixed-chromium-chroot-seccomp.json adds exactly one unconditional SCMP_ACT_ALLOW rule for chroot, leaves the original upstream file unchanged, and grants no capability. Its bytes must equal the canonical reconstruction from the independently pinned upstream original plus that one constant rule; all deviations fail closed. Evidence records the variant, derived SHA-256 and original SHA-256 and Git blob. No generated-source runner or registry uses this derivative. All other Docker arguments and eleven compatibility checks remain unchanged. The hypothesis is not confirmed until the real owner-host probe succeeds; even then, generated execution remains unauthorized.

## Post-chroot owner-host result

Owner-host output confirms the approved fixture-only variant removes both SIGABRT startup failures. The two browser checks still lack affirmative rendered evidence, despite normal process exits. The probe now supplies a bounded virtual-time budget and navigation timeout (within the existing outer process timeout), following the existing trusted Calculation Journal capture approach. Failed evidence records distinguish empty/blank/unchanged/error DOM, negative or pending sandbox status, child-renderer failure, and PID/memory event deltas even when the browser process exits zero. No raw log or rendered HTML leaves the guest. This does not assert that capture timing was the sole cause. No further security policy or resource limit changes, and no generated-source execution. All eleven original checks still must pass.

## Owner-approved fixed-fixture PID ceiling

The subsequent owner-host run reported 9/11, normal Chromium exits, USER_NAMESPACE_OK and PID_LIMIT_HIT for both browser invocations. Kernel cgroup event deltas demonstrate that the existing 96-task limit was reached; this includes threads. The owner approved a bounded increase to 256 for M4.7's fixed fixture only. The exact ceiling is enforced by the Docker argument and recorded in the evidence. The two-CPU and 2 GiB memory limits, timeouts, seccomp policy, non-root identity, zero capabilities, no network, read-only root, and no mounts remain unchanged. No other guest or runtime profile is modified. Actual compatibility is still pending owner-host results; no generated-source execution is authorized even on 11/11.

## Owner-host 10/11 — sandbox WebUI capture ceiling

After the approved PID ceiling increase, the owner observed actual fixed DOM PASS, all nine isolation checks PASS and confirmed cleanup. The remaining status probe exited normally but was classified DOM_OUTPUT_OVERSIZED. The full Chromium internal WebUI contains substantially more markup than the tiny HTML fixture. The status-only capture ceiling is now 256 KiB (byte-counted), while the fixed DOM remains capped at 32 KiB. The browser process retains its existing wall-clock timeout and container limits. No raw DOM leaves the guest. The existing affirmative status assertion remains mandatory and negative/pending/oversized evidence still blocks. No changes to the approved seccomp profile, PID ceiling, container capabilities, network or runtime authorization. A subsequent owner-host result is required to demonstrate the final check.
