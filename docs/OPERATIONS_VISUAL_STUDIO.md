# Visual Studio 2026 — Windows development

Open the repository-root **CodingAssistant.sln** file (double-click in File Explorer,
or **File > Open > Project/Solution** in Visual Studio). Use **Open Project/Solution**,
not **Open Folder**. The solution loads two existing source trees:

- **CodingAssistant.Api** — the existing .NET 10 ASP.NET Core project, `src/CodingAssistant.Api/CodingAssistant.Api.csproj`.
- **CodingAssistant.Web** — the existing React/TypeScript/Vite app, exposed through `src/CodingAssistant.Web/CodingAssistant.Web.esproj`.

Prerequisites: Visual Studio 2026 with **ASP.NET and web development** (including
the JavaScript/TypeScript project system), .NET 10 SDK, Node.js/npm, and running
local Ollama with `qwen3.5:4b` installed. The JavaScript SDK is pinned so the
solution restores the same project-system package on each machine.

## First-time source build

From **PowerShell** in the repository root:

```powershell
cd "$HOME\source\repos\CodingAssistant"
git pull --ff-only
dotnet --version
node --version
ollama list
```

Open `CodingAssistant.sln` in Visual Studio. **F5 now also verifies the frontend**: in a Windows Debug checkout it compares a SHA-256 fingerprint of current React/Vite sources to the last successful generated UI. If that build is missing, outdated, or doesn't contain the automatic sign-in client, startup runs `npm run build` (installing frontend dependencies first only when necessary) and serves **the freshly generated source-tree UI**, not an old `bin/Debug/net10.0/wwwroot` copy. A failed build stops startup instead of silently showing the old token-entry screen.

Building the full solution with Ctrl+Shift+B still works and creates the Vite output in `src/CodingAssistant.Api/wwwroot`. On repeated F5 runs without source changes, the frontend build is reused. The SHA fingerprint contains no credentials. This developer-only behavior does not change published/Release binaries or Linux.

Do not run npm in `C:\Users\<username>` or the repository root: the
`package.json` is inside `src/CodingAssistant.Web`.

## Starting locally — one double-click (recommended)

From File Explorer, double-click the **`Start-CodingAssistant.cmd`** file in the repository root, next to `CodingAssistant.sln`.

The launcher uses Windows PowerShell 5.1 and:
1. Checks .NET/npm availability and whether port 5098 is already occupied.
2. Builds the React UI from the current checkout (installs frontend packages on first launch, retries a failed build once after refreshing packages).
3. Reuses a **stable, cryptographically random 64-character owner token** encrypted with Windows DPAPI **CurrentUser** under `%LOCALAPPDATA%\CodingAssistant\owner-token.dpapi`. The same account's Visual Studio F5 process reads that credential automatically; no manually defined environment variable or `launchSettings.json` secret is needed.
4. Keeps that DPAPI-protected owner token internal to the server—**nothing is printed or copied to the clipboard**.
5. Explicitly disables Windows autonomous code execution and starts the .NET API in that console.

Open **http://127.0.0.1:5098**. Mission Control **connects automatically**—no pasted token, username, password or Connect click. Leave the launcher console open; Ctrl+C stops the API. Restarts reuse the same protected owner credential. The browser obtains a temporary 12-hour same-origin session in memory without ever receiving the long-lived owner token. Refreshing the page obtains a new temporary session automatically; no cookies, localStorage, token URL parameters or credentials in logs.

You can also start the exact same workflow from PowerShell:

```powershell
cd "$HOME\source\repos\CodingAssistant"
.\scripts\windows\Start-CodingAssistant.ps1
```

If PowerShell blocks direct script invocation under your local execution policy, double-click the repository's `Start-CodingAssistant.cmd` wrapper. Its execution-policy override applies only to the new PowerShell process for this checked-out script.

## Confirm you're not running an old API process

After pulling current `main`, **stop any older CodingAssistant server** before pressing F5. The new process reports a harmless, fixed UI version when you open `http://127.0.0.1:5098/healthz`:

```json
{"status":"ok","executionEnabled":false,"uiVersion":"windows-auto-session-v1","uiSource":"source-current"}
```

If `uiVersion` is absent, you're still connected to an **older API process** or an outdated checkout. Stop it and launch the current version; a browser hard reload cannot upgrade a server that hasn't been restarted. If `uiVersion` is present but `uiSource` is `packaged`, you're running a packaged build instead of the current Windows Debug checkout. The old copied frontend is no longer silently preferred.

## Starting directly from Visual Studio F5

**F5 now works without manual token configuration.** The API automatically creates or reuses the same Windows DPAPI CurrentUser credential that the launcher uses. You can set `CodingAssistant.Api` as the startup project, press **F5**, and browse to `http://127.0.0.1:5098`. No copied `launchSettings.json`, hard-coded token or hand-created environment variable is required.

After pressing F5, simply open **http://127.0.0.1:5098**. A Windows-local, same-origin browser session is established automatically. There is no login ceremony and you never need to use `Copy-CodingAssistant-Token.cmd` during normal operation. That older helper is retained for exceptional troubleshooting and non-browser API clients only.

Browser protection: the session bootstrap requires the exact `127.0.0.1` Host and Origin, a same-origin browser fetch-metadata header, and a non-simple app header (no cross-origin CORS). It issues a short-lived HMAC-authenticated token, **not** the stored DPAPI owner secret. Other API endpoints and SignalR still require authenticated bearer requests; the browser holds its temporary bearer in memory only. This protects against ordinary unrelated websites but **does not protect against hostile local programs that can directly make HTTP requests and forge headers**. CodingAssistant remains a single-user, trusted-local-machine application; use OS account security and do not expose it on a shared/untrusted computer.

If you deliberately provide `CODINGASSISTANT_OWNER_TOKEN`, that explicit value still takes precedence; an invalid nonempty value is rejected rather than silently overridden. Stop the existing API before switching between F5 and the launcher on the same port. The credential is shared, but two processes cannot both listen on 5098.

If the encrypted credential is corrupted or belongs to another Windows account, startup fails clearly instead of rotating it behind your back. To intentionally reset it, stop the API, remove `%LOCALAPPDATA%\CodingAssistant\owner-token.dpapi` from your own profile, then start the app again. **A reset invalidates the earlier browser token**; it does not delete SQLite projects/history. Never commit or share that credential file.

If the terminal reports **port 5098 is already in use**, stop the other local API before relaunching; generating another token without stopping that instance would not let you sign in. The launcher does not modify the server's SQLite data directory or perform a Git pull automatically.

The **Windows native execution runner is disabled by design**. Interviews,
specifications, and text-only source drafts use the local Ollama API; the
autonomous OpenClaw/ACPX/OpenCode build/test runner still requires separate
Windows/WSL2 OS-isolation qualification. Do not enable it unsandboxed.

Windows and Dell each have separate owner-private SQLite data directories.
Git does not synchronize task history or run evidence.

The existing Linux `start.sh`, CI scripts, .NET project, and Vite project
remain otherwise unchanged.
