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

## Simplest launch on the Dell

Run **one line** from the existing checkout:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m3/launch.py
```

The launcher reuses the installed M3 build if present, installs an already
downloaded GitHub Actions ZIP if available, or downloads a successful artifact
with authenticated `gh`. If GitHub CLI is not available/authenticated, it opens
the exact Actions page for a one-time browser ZIP download and then continues
installation when you press Enter. It opens Firefox and prints a temporary
owner token to paste into the login screen. It does **not** store the token,
expose the API beyond 127.0.0.1, or enable AI execution by default. Ctrl+C
stops only the API process that the launcher started.

For the next **controlled, fixed-fixture** Dell coding acceptance run, use
`python3 tools/m3/launch.py --enable-execution`. This opt-in first runs the
existing M2→M1 model-free rootless Podman negative boundary probes and
prepares the pinned offline derivative against the current source fingerprint.
If any check fails, the application does not start in execution-enabled mode.
The full-hash M1 executable plan still requires separate owner approval,
followed by an explicit start click. No arbitrary repository is allowed.

These prerequisites do **not** constitute all ten required integrated issue
#76 adversarial tests. M1 overall stays YELLOW until that release/security
gate and its real Dell evidence are complete.

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
