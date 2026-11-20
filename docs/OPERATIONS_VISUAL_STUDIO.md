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

Open `CodingAssistant.sln` in Visual Studio, then **Build > Build Solution**
(Ctrl+Shift+B). Building the JavaScript project runs npm install and
`npm run build`. Vite places the compiled UI in ignored
`src/CodingAssistant.Api/wwwroot`, where the API serves it.

Do not run npm in `C:\Users\<username>` or the repository root: the
`package.json` is inside `src/CodingAssistant.Web`.

## Starting locally — one double-click (recommended)

From File Explorer, double-click the **`Start-CodingAssistant.cmd`** file in the repository root, next to `CodingAssistant.sln`.

The launcher uses Windows PowerShell 5.1 and:
1. Checks .NET/npm availability and whether port 5098 is already occupied.
2. Builds the React UI from the current checkout (installs frontend packages on first launch, retries a failed build once after refreshing packages).
3. Reuses a **stable, cryptographically random 64-character owner token** encrypted with Windows DPAPI **CurrentUser** under `%LOCALAPPDATA%\CodingAssistant\owner-token.dpapi`. The same account's Visual Studio F5 process reads that credential automatically; no manually defined environment variable or `launchSettings.json` secret is needed.
4. Copies that token to the clipboard **without printing or logging it**.
5. Explicitly disables Windows autonomous code execution and starts the .NET API in that console.

Open **http://127.0.0.1:5098**, press **Ctrl+V** into **Owner session**, and click **Connect locally**. Leave the launcher console open; Ctrl+C stops the API. Restarts reuse the same owner credential, so you no longer have to generate one every time. It is **not stored as plaintext**, embedded in source/config/URLs or printed in logs. The clipboard temporarily holds the token, so avoid pasting it into unrelated software.

You can also start the exact same workflow from PowerShell:

```powershell
cd "$HOME\source\repos\CodingAssistant"
.\scripts\windows\Start-CodingAssistant.ps1
```

If PowerShell blocks direct script invocation under your local execution policy, double-click the repository's `Start-CodingAssistant.cmd` wrapper. Its execution-policy override applies only to the new PowerShell process for this checked-out script.

## Starting directly from Visual Studio F5

**F5 now works without manual token configuration.** The API automatically creates or reuses the same Windows DPAPI CurrentUser credential that the launcher uses. You can set `CodingAssistant.Api` as the startup project, press **F5**, and browse to `http://127.0.0.1:5098`. No copied `launchSettings.json`, hard-coded token or hand-created environment variable is required.

To sign into a **new browser session** after pressing F5, double-click **`Copy-CodingAssistant-Token.cmd`** (next to the solution). It places the existing credential on the clipboard without changing the server token or printing a secret. Then paste once into the Owner session box. The React UI deliberately keeps bearer tokens only in tab memory; it does not persist them in browser local storage. Reconnecting after a refresh may still require pasting from the copy helper.

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
