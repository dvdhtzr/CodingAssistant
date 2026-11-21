# Mission Control (M3)

React + TypeScript, served by the existing ASP.NET Core API on **IPv4 loopback**.
No mock coding engine, token-in-URL, cloud inference, extra database or server.

On Windows, Mission Control connects automatically on `http://127.0.0.1:5098`.
A strict same-origin POST bootstrap returns a short-lived 64-character
browser bearer (HMAC-authenticated, 12-hour expiry) while the persistent
DPAPI owner token stays server-side. No typing, clipboard or login UI.
Sessions are stored only in React memory: no localStorage, cookies, URL
parameters or log output. A browser reload obtains a new session. On Linux,
the existing explicit owner-token login remains unchanged. Browsers cannot
supply arbitrary headers to WebSocket constructors, so SignalR uses
**LongPolling with Authorization headers**, plus durable numbered SQLite event
replay after watch/reconnect.
A disconnected notification channel never implies a lost run.

Planning hash consent and executable hash consent are entirely separate;
even executable authorization does not automatically start a model. The UI
does not grant arbitrary-repository or model access. The backend's existing
fixed-fixture scope, local-execution opt-in and Podman protections remain in force.

## Start, stop, restart (Dell)

**First time after checking out the current release:**

```bash
cd ~/CodingAssistant && git pull --ff-only && bash start.sh
```

**Every later launch:** select **CodingAssistant** from the KDE application
menu, or run `bash ~/CodingAssistant/start.sh`. One command, no flags.
The startup script installs a desktop menu entry once; checks whether the
server is already running; fast-forwards clean main when online; installs the
newest matching *official checksummed GitHub Release* (or keeps an installed
version offline); runs real model-free isolation prerequisites; opens Firefox;
and shows a fresh owner token to paste into the login form.

Ctrl+C closes the API and lets it reconcile an interrupted attempt on the
next start. Persisted SQLite tasks and event history survive. **A live model
run is never automatically resumed or retried.** If a run is
`RecoveryBlocked`, use its explicit safe reconciliation button and start a
new approved run only after the security gate clears.

The launcher requests **only the fixed, already approved M1 disposable
fixture**; it cannot run in arbitrary user repositories. A failed isolation
preflight opens read-only Mission Control instead of secretly bypassing the
gate. Even when coding is enabled, planning consent, exact executable-hash
consent, and an explicit Start click remain separate.

Issue #76's ten integrated adversarial security checks remain required
before wider autonomous execution. This is a development preview.

## Approved specification → visible, source-only project draft

The first task-specific generation slice now follows the interview:
**Create task → local AI interview → versioned specification → owner
approval → generate a small project draft → inspect files/events → download
ZIP.** The local model chooses up to five relative source filenames from
the specification, then writes each file in separate local Ollama requests.
No filename, fixture or tech stack is hard-coded by the product. The result
and every real event (model call launched, path selected, file saved, source
statically checked) are saved in owner-private SQLite and survive restart.
Mission Control polls the durable history and displays actual file contents
and a download link. Cancel and process interruption never auto-retry.
A revised specification invalidates the approval for **new** builds.

**Security and truthful status:** This is a **draft source producer**, not
yet the qualified autonomous OpenClaw/ACPX/OpenCode build/test agent.
Generated text is **untrusted** and remains in SQLite until explicitly
downloaded. It is **never executed, restored, compiled, launched, mounted,
applied to another repository, or sent to a remote model**. The application
validates path traversal/extension/size/count, JSON source syntax, bind to
approved current SHA/version, model allowlist, single active job and fixed
loopback network. It must say `Generated (NOT TESTED)`, not `Succeeded`
or `Verified`. The real isolated coding/build/test integration and issue
#76 ten integrated adversarial security gates are still required for
Baseline 1.0, as tracked in #92. Do not claim this draft equals a tested
or runnable application.

## Current primary workflow: freeform objective → dynamic interview → specification

Starting at issue #92, the primary Mission Control UI now includes
`InterviewStudio` for every newly created task (not an HTML or C# fixture).
The local Ollama model conducts an adaptive requirements interview based on
the **actual task objective and saved owner answers**. The conversation is
stored in owner-private SQLite with monotonically numbered messages and
optimistic concurrency. A stale browser tab cannot silently append a second
answer or approve a different specification. The user may draft a detailed
Markdown implementation specification after answering at least one question,
then review and approve the **exact version and SHA-256** using a confirmation
checkbox. Changing requirements supersedes prior approvals. Reconnect and API
restart preserve the conversation, specification and consent.

Models are explicitly restricted to installed local `qwen3.5:4b` and
`qwen2.5-coder:3b`; no proxy, redirect, cloud or model tool execution.
Each interview/spec request has a bounded prompt and timeout; failures do
not append imaginary assistant replies or claim the model is progressing.

**Important limitation:** This release implements the interview and
specification side of Baseline 1.0, **not** its generalized build engine.
The new build panel states this explicitly. The old single-file HTML demo
and frozen C# agent qualification are in separate Advanced disclosures and
must not be presented as arbitrary-project implementation.
The remaining build milestone requires a task-bound approved workspace,
a real OpenClaw→ACPX→OpenCode coding run, OS-denied escape cases (#76),
truthful live file/build/test results and inspectable artifacts (#92).
No repo scope is widened or model permission added in this PR.

## First useful task: standalone HTML artifact

The top of Mission Control now has a **Make your HTML page** action.
It uses the selected task's actual objective to request one standalone
`index.html` from local Ollama, shows the source, and offers a download.
The output is saved in owner-private SQLite and survives restart.

No plan/hash approval is needed: this is bounded TEXT generation only.
It never executes generated code, edits a repository, runs a shell,
mounts a project, starts OpenClaw/ACPX/OpenCode or contacts cloud models.
Only installed qwen3.5:4b (default) and qwen2.5-coder:3b are allowed.
An unavailable model is an explicit error, not a silent provider fallback.
The Ollama HTTP client uses loopback without proxy or redirects; the
response has hard size/time bounds, rejects scripts, external resources
and active HTML, and is shown only as plain text within Mission Control.
The downloaded file is a draft, not independently verified source.

The old M1 fixed C# repair approvals have been moved to an Advanced
section explicitly labeled **not your HTML task**. That agent always
uses the 02-build-repair fixture, and does not read the user objective.
Arbitrary repository-scoped agent coding requires separate security
qualification and remains a future milestone.

## Run output and code changes

After selecting a run, Mission Control now shows a prominent **Run result**
panel and a **View run output** shortcut. On sign-in it reopens the most
recent task/run automatically. While running, the panel clearly says output
has not been produced yet. Once complete, the authenticated
`GET /api/runs/{id}/result` endpoint asks the existing owner-private M1
ledger for a **read-only**, strict-run-ID/executable-hash-bound result summary:
final status, last stage, safe stop category, independent offline .NET verdict,
whether the disposable C# source changed, and verified local inference count.
No raw agent conversation, credentials, arbitrary filesystem paths or stderr
are published. The endpoint also works after restarting into history-only
mode on the Linux Dell; it never starts a model.

For newly completed **independently verified** runs, the fixed
`02-build-repair/api/Program.cs` diff (up to 7KB) is retained in the private
M1 evidence artifact **before deleting the disposable worktree**. The UI shows
that patch as plain text. Unverified, unapproved, oversized or arbitrary-scope
source cannot be displayed as a verified change. Prior runs can display their
archived summary/evaluator evidence but have no retained diff; show that
limitation instead of claiming the output is missing or inventing the code.
An uncertain cleanup remains `RecoveryBlocked`, not a successful result.

## Live activity and local model comparison

Running jobs show a **local clock and indeterminate pulse**. These are not
claims that a model is responding. A separate, append-only event records each
fixed, host-produced milestone: pinned scope checked, disposable copy ready,
Ollama socket ready, Gateway launch/readiness, coding request dispatched and
returned, guest removed, and offline evaluator started/passed. Never show
raw model text, token contents, paths outside the fixture, credentials or
container stderr. If no confirmed milestone appears for 90 seconds, the UI
explicitly warns that the worker might be waiting or stalled. Timestamps,
durable SQLite event ordering and cancellation remain available.

To compare performance *outside* the approved M1 engine, finish or cancel any
active coding job, then use:

```bash
cd ~/CodingAssistant && python3 tools/m3/compare_coder.py --pull-coder
```

This explicitly downloads `qwen2.5-coder:3b` once if missing. The probe
makes **one short local-only Ollama tool-call C# repair per model** versus
`qwen3.5:4b`, reports elapsed time/tokens-per-second/tool-call success,
and stores only bounded metrics in ignored `artifacts/m3/`. No generated
code is executed, no remote inference is used, and no M1 model pin or owner
approval is changed. Short inference success is **not** proof that the model
works with OpenClaw/ACPX/OpenCode or passes the original M0 gates. A separately
reviewed and qualified model profile is required before enabling it for real
agent runs.

The normal desktop shortcut or `bash ~/CodingAssistant/start.sh` updates the
installed application whenever a matching verified release is available,
without a separate `--refresh` or GitHub CLI authentication step. Failed
updates leave the last verified app intact and never modify the SQLite DB.

## Development

```bash
cd src/CodingAssistant.Web
npm install
npm run dev
```

Run the .NET 10 API separately on 127.0.0.1:5098 with an owner token.
Vite binds only 127.0.0.1 and proxies /api in development; do not expose
the dev server to the LAN. For a same-origin production build:

```bash
cd src/CodingAssistant.Web
npm install
npm run build
cd ../CodingAssistant.Api
dotnet run
```

This generates ignored `src/CodingAssistant.Api/wwwroot/` assets. The CI
workflow builds frontend assets **before** .NET smoke/publish and ships them
with the self-contained API. Fetching history and cancelling runs remains
available when the local execution runner is disabled; real model work is
never invoked in CI. Diffs/evaluator artifacts are not returned by today's
API and are deliberately not fabricated in this UI.
