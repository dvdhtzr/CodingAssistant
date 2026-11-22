# M4.3 — Windows independently audited disposable project workspace

**Status: CI candidate; requires separate real Windows Docker Desktop owner-host acceptance.**
This is a fixed, reviewed mini-project fixture. It does **not** execute user
or AI-generated source, authenticate agents or enable the Static Web runtime.

## Prerequisite: confirmed M4.2 owner-host result

The owner reported a genuine Windows Docker Desktop fixed-fixture result after
the M4.2c Chromium repair: **18/18**, Chromium HEADLESS_SHELL with both
working/broken PASS, and confirmed disposable container cleanup. The private
evidence file remains on the Windows host in the CodingAssistant
qualifications folder. This proves bounded trusted fixture behavior only.
It is not independent verification of untrusted browsing or generated code.

## Purpose of this step

Before sending even a sandboxed model-generated Static Web application
through Chromium, validate that Docker Desktop can mount a **fresh
disposable, strictly scoped source snapshot**, allow only changes to that
snapshot, deny access to adjacent host data and reject independent-host
file-audit failures.

The M4.3 host creates a unique, freshly generated directory under the
OS temporary directory containing:

- approved/seed.txt and approved/notes.txt — the only files exposed to guest
- outside/keep.txt — adjacent sentinel, deliberately **not mounted**

It pins the same locally cached official Playwright image to its immutable
local image ID, uses the verified Windows local Docker Desktop context and
starts exactly one nonroot Linux guest with no network, read-only root,
capabilities dropped, no privilege escalation, private IPC, finite
memory/PID/CPU/runtime limits and a small private tmpfs. The sole host bind
mount is the randomly created approved/ directory to /workspace. It never
mounts the original CodingAssistant repository, a real user's project,
host home, .git metadata, a Docker socket, personal files or secrets.

A separately reviewed and SHA-recorded **fixed** Node script is streamed
through Docker stdin, not a command-line argument. It creates a small
trusted HTML+JavaScript calculation page, executes it in the fixed browser
for one real external-JS click check, rewrites an approved seed file and
writes a second file from a child process. It independently attempts
forbidden sibling reads/writes, a symlink escape, root filesystem writes,
a subprocess root filesystem write, outbound TCP, provider credential
inheritance and Docker socket and Git metadata visibility.

The guest must report **17/17 exact Boolean cases**. Then the **Windows
parent independently audits 9 checks** on real host files: exact allowlist,
approved seed change, generated HTML/JS, child file, unchanged notes,
unchanged unmounted sibling sentinel (with expected hash), symlink target,
and absence of .git. All must pass. The host removes its exact owned guest,
checks it is absent, removes its unique temporary project and verifies
that directory is absent. Uncertain cleanup blocks success.

This is **not** execution of arbitrary source or a fully qualified browser
sandbox: Chromium uses --no-sandbox only for this reviewed and frozen
mini-project fixture. It cannot be used for untrusted model-generated
files. No immutable owner-approved real task scope or independent untrusted
evaluator has been built here.

## Owner host acceptance

After this PR is green, merged and present on main, with Docker Desktop
running in Linux-container mode:

~~~powershell
git switch main
git pull --ff-only
node tools/m4/windows_scoped_workspace_qualification.mjs --run
~~~

The script with no arguments does nothing, prints NOT_RUN and does not
call Docker. It never pulls an image or invokes a model. If Docker
rejects the temporary bind mount, the child tests fail, or the audit
detects an escape, the correct outcome is BLOCKED, with only bounded
safe categories and Boolean evidence saved privately under
%LOCALAPPDATA%\CodingAssistant\qualifications.

Expected **real** PASS, not a CI claim:

~~~text
M4.3 WINDOWS_SCOPED_FIXTURE_PASS
  guest cases: 17/17
  independent host checks: 9/9
  container absent: true; fixture removed: true
  generated source / original repo: NOT_AUTHORIZED
  Static Web runtime: UNQUALIFIED
~~~

## Gates still outstanding

1. Independently qualified untrusted Chromium sandbox and evaluator/toolchain
   image(s), with source package/asset safety checks.
2. Verified temporary, exact owner-approved model-generated source snapshot,
   linked to task ID, version, plan hash, image digest and allowed files;
   no original repository writes.
3. M2→M1 #76's integrated ten adversarial cases, symlink/TOCTOU, child,
   outbound network/Git, cancellation/crash reconciliation and disable-on-fail.
4. #92 exact task-driven scope, separate owner approval and immutable
   workspace/plan admission.
5. #111 genuine independent browser acceptance on generated Calculation
   Journal files and at most two bounded, evidence-driven repairs.

CI tests only review command policy, fixed guest syntax and simulated
fail-closed host observations. **CI does not launch real Docker Desktop**,
and success never changes RuntimeProfileRegistry qualification flags.
