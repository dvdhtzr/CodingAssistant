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

## Starting locally

The API intentionally requires a **random 64-character lowercase hex bearer
token** in `CODINGASSISTANT_OWNER_TOKEN`. Do not commit it to source,
put a static token in `launchSettings.json`, or expose ports to the LAN.
For a first run, use a PowerShell terminal **from the repository root**:

```powershell
$bytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes)
$rng.Dispose()
$env:CODINGASSISTANT_OWNER_TOKEN = ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
$env:CODINGASSISTANT_ENABLE_LOCAL_EXECUTION = '0'
Set-Clipboard $env:CODINGASSISTANT_OWNER_TOKEN
dotnet run --project .\src\CodingAssistant.Api\CodingAssistant.Api.csproj
```

Open `http://127.0.0.1:5098` and paste the copied token into Mission Control.
The frontend and backend run together through the API's same-origin static
files; a separate Vite development server is not required. If you later
choose to launch the API with F5 inside Visual Studio, it must inherit a
valid owner token from the process environment. A Visual Studio solution
does not bypass that authentication requirement.

The **Windows native execution runner is disabled by design**. Interviews,
specifications, and text-only source drafts use the local Ollama API; the
autonomous OpenClaw/ACPX/OpenCode build/test runner still requires separate
Windows/WSL2 OS-isolation qualification. Do not enable it unsandboxed.

Windows and Dell each have separate owner-private SQLite data directories.
Git does not synchronize task history or run evidence.

The existing Linux `start.sh`, CI scripts, .NET project, and Vite project
remain otherwise unchanged.
