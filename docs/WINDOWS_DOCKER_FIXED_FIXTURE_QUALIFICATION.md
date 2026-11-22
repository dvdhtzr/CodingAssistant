# M4.2 — Windows Docker Desktop fixed-fixture qualification

**Status: pending real Windows-host acceptance.** This is a qualification probe, not a general evaluator, model-code executor or VERIFIED evidence.

## One-command Windows workflow

Windows is our primary development machine. Docker Desktop must be running in Linux containers mode. If the official browser image is not yet cached, the owner may **explicitly** run the following one-time public image download. Nothing in the probe downloads an image or software:

~~~powershell
docker pull mcr.microsoft.com/playwright:v1.65.1-noble-amd64
~~~

Then, from the existing Windows CodingAssistant checkout:

~~~powershell
git switch main
git pull --ff-only
node tools/m4/windows_docker_qualification.mjs --run
~~~

No owner token, LLM, file edits, API calls, project folders, or hash copy-paste are required. Without --run the script prints NOT_RUN without invoking Docker. If Docker Desktop or the cached image is missing, it reports BLOCKED. It is not wired to a Mission Control execution button.

## Execution and evidence

The host refuses remote Docker endpoints and DOCKER_HOST / DOCKER_CONTEXT overrides. It requires a Windows named-pipe Docker Desktop **Linux** engine with memory/PID limits, and validates the locally cached Microsoft Playwright image's immutable image ID and registry digest. Only this ID is used to launch one container: --pull=never, --network=none, --read-only, --cap-drop=all, no-new-privileges, nonroot uid/gid 1000, private IPC/PID, CPU/memory/PID budgets, disposable tmpfs and **no host mounts, published ports, credentials or engine socket**.

A checked-in fixed guest runs 18 bounded Boolean observations: JS valid/invalid syntax and absence of syntax-check side effects; real Chromium executing a trusted working button handler and confirming a broken fixture stays inert; nonroot identity; blocked system/symlink/traversal writes and credential reads; missing secret environment, engine socket and Windows host paths; denied external TCP, host Ollama loopback, child network/system writes and remote Git. Browser tests use --no-sandbox **only with trusted fixtures**; this does not qualify untrusted Chromium execution.

The host records the exact image ID, registry digest, fixed guest script SHA-256, each Boolean result and cleanup. It always attempts to remove its randomly named guest and independently checks Docker's container list. Any missing/failed test, timeout, malformed output, wrong image or uncertain cleanup is BLOCKED. Compact private reports are written to %LOCALAPPDATA%\CodingAssistant\qualifications (no raw logs or secrets).

Expected **real owner-host** success:

~~~text
M4.2 WINDOWS_DOCKER_FIXED_FIXTURE_GUEST_PASS
  fixed guest cases: 18/18
  container absent after run: true
  generated code and untrusted browser: NOT_AUTHORIZED
  Static Web runtime profile: UNQUALIFIED
~~~

Passing model-free Windows/Ubuntu CI only verifies the Docker command builder, admission rules, strict result parser and fail-closed cleanup with **simulated** Docker responses. CI never starts a Docker guest.

## M4.2a — Windows guest input and bounded diagnostics

An owner-host run on November 21, 2026 reached the cached Playwright
image, but reported `0/18`, `Guest observations absent or oversized`
and confirmed guest removal. This means the checks did not produce
auditable observations; it is **not** an 18-case security failure or PASS.

The Windows host now sends the unchanged, checked-in Node guest script
through Docker standard input (`docker run -i` and
`node --input-type=module - --run-fixed-guest`) instead of embedding
the entire multi-line program in a Windows command-line argument.
The input is always the same trusted source whose SHA-256 is recorded
in the report; it cannot be supplied by a model or task. The disposable
`/tmp` mount explicitly uses writable mode 1777 for UID 1000.

If Docker still cannot launch Node, the runner records the bounded
Docker exit code plus an **allowlisted error category** (for example,
`NODE_EXECUTABLE_UNAVAILABLE`, `CONTAINER_START_FAILED`,
`GUEST_PERMISSION_DENIED` or `GUEST_ARGUMENT_REJECTED`) rather
than persisting or printing Docker's potentially sensitive raw stderr.
When a valid fixed guest reports a failed case, the report names only
the failed Boolean check identifiers. The 18 exact checks and required
independent container cleanup have not been relaxed.

**CI is a simulated Docker contract test**, plus a real, harmless local
Node stdin/argument round-trip. The next real owner-host run is still
necessary; do not mark Windows QUALIFIED based on CI.

## M4.2b — Docker PID mode compatibility fix

On the owner's real Windows Docker Desktop host, M4.2a returned
`docker exit=125; category=UNCLASSIFIED_GUEST_FAILURE` with
`0/18` and confirmed cleanup. This indicates Docker rejected the
container launch before the guest returned evidence.

Inspection found an invalid explicit Docker argument:
`--pid=private`. Docker's CLI documents only
`--pid=host` and `--pid=container:...` overrides. The default
Docker PID namespace is already private, so the runner **removes**
`--pid=private` and retains the default private PID isolation,
`--ipc=private`, `--pids-limit=96` and all other restrictions.
Cross-platform model-free tests now reject **any explicit PID mode**
and check classification of `invalid PID mode` daemon errors.

This is a likely root cause, not a confirmed fix until another real
Windows Docker Desktop attempt. The result must remain BLOCKED if
the host still cannot launch the guest. See official
[Docker PID namespace documentation](https://docs.docker.com/reference/cli/docker/container/run/#pid-settings---pid).

## Still required before model-generated source may execute

- Independently pinned Node/Playwright/Chromium evaluator and reviewed dependencies with browser sandbox suitable for untrusted source
- Genuine Windows host positive/negative security tests with an exact disposable source snapshot and **no original repo mount**
- Mandatory integrated ten-case #76 adversarial suite, including symlink/path/network/child/Git and cancellation/crash recovery
- #92 separately approved exact task/spec/plan/source/image scope
- #111 actual browser acceptance evidence on generated artifacts and at most two source repair attempts

Both the Static Web registry and UI must remain UNQUALIFIED / NOT TESTED. A successful fixed fixture is not a fully qualified runtime. Existing Linux qualification is deferred, not discarded.

Reference: https://playwright.dev/docs/docker and https://docs.docker.com/desktop/features/wsl/ .
