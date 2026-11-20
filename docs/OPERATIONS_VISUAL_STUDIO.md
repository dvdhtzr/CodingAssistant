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
1. Checks .NET/npm availability and whether port 5098 is already occupied, avoiding the confusing case of a token generated for a different running server.
2. Builds the React UI from the current checkout (installs frontend packages on first launch, retries a failed build once after refreshing packages).
3. Generates a **fresh cryptographically random 64-character lowercase hex owner token** for this API process.
4. **Prints the entire token in green in the console and copies it to the clipboard.**
5. Disables Windows autonomous execution, then starts the .NET API in that same console.

Open **http://127.0.0.1:5098**, press **Ctrl+V** in **Owner session**, and click **Connect locally**. Leave the launcher terminal open; use **Ctrl+C** to stop the API. Every new launch prints and copies a new session token, so you do not need to generate it yourself or modify `launchSettings.json`. The token is **not** written to source, a file, the URL, or application logs. As with any clipboard secret, avoid pasting it into unrelated software.

You can also start the exact same workflow from PowerShell:

```powershell
cd "$HOME\source\repos\CodingAssistant"
.\scripts\windows\Start-CodingAssistant.ps1
```

If PowerShell blocks direct script invocation under your local execution policy, double-click the repository's `Start-CodingAssistant.cmd` wrapper. Its execution-policy override applies only to the new PowerShell process for this checked-out script.

The Visual Studio solution remains available for edits and normal builds. Starting the API with **F5 directly in Visual Studio** does **not** automatically run the separate launcher or create a token; use the launcher for routine testing, or explicitly configure a secure per-process environment if debugging the API with F5.

If the terminal reports **port 5098 is already in use**, stop the other local API before relaunching; generating another token without stopping that instance would not let you sign in. The launcher does not modify the server's SQLite data directory or perform a Git pull automatically.

The **Windows native execution runner is disabled by design**. Interviews,
specifications, and text-only source drafts use the local Ollama API; the
autonomous OpenClaw/ACPX/OpenCode build/test runner still requires separate
Windows/WSL2 OS-isolation qualification. Do not enable it unsandboxed.

Windows and Dell each have separate owner-private SQLite data directories.
Git does not synchronize task history or run evidence.

The existing Linux `start.sh`, CI scripts, .NET project, and Vite project
remain otherwise unchanged.
