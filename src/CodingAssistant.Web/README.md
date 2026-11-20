# Mission Control (M3)

React + TypeScript, served by the existing ASP.NET Core API on **IPv4 loopback**.
No mock coding engine, token-in-URL, cloud inference, extra database or server.

The owner enters a 64-character API bearer token per browser session. It stays
only in React memory: no localStorage, cookies, URL parameters or log output.
On reload the owner must reconnect. Browsers cannot supply arbitrary headers
to WebSocket constructors, so SignalR uses **LongPolling with Authorization
headers**, plus durable numbered SQLite event replay after watch/reconnect.
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
