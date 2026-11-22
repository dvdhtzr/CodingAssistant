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
the **byte-pinned official Playwright seccomp profile** (default-deny
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
## One command on Windows after GitHub CI green and PR merge

From the existing CodingAssistant checkout, with Docker Desktop
running in Linux-container mode:

~~~powershell
git switch main
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
  Chromium: HEADLESS_SHELL; fixed DOM: PASS; sandbox status: PASS
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
