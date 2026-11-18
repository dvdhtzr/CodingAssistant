# Windows 11 — primary inference host setup

Use PowerShell with Python 3.10+ and existing Ollama.

Read-only inventory:
```powershell
ollama list
ollama --version
dotnet --version
node --version
npm --version
opencode --version
py -3 -m unittest discover -s tests -v
py -3 tools/m0/preflight.py --model qwen3.5:9b
```
Missing commands are diagnostic. Install .NET SDK 10 from https://dotnet.microsoft.com/download/dotnet/10.0 only when needed; OpenCode official Windows installation guidance is at https://opencode.ai/docs/. M0 does not require OpenClaw yet.

After confirming model exists:
```powershell
py -3 tools/m0/inference_probe.py --model qwen3.5:9b
```

For future Dell ↔ Windows hybrid mode, keep Ollama localhost-only initially. Any remote model endpoint needs approved private networking and access protection; never open Ollama's port to the public internet.

**Do not run autonomous benchmark on an ordinary Windows user profile.** Agent code execution and build/test steps are not confined until OS-level sandboxing is independently validated.
