# M0.3 — OpenCode discovers the local Ollama model

**This is a model-discovery test, not a coding benchmark.**

After the owner reports that the host preflight, single Qwen tool-call probe, .NET 10 and OpenCode checks are green, test that **OpenCode itself** sees the intended model.

On the Dell, from the repository:

```bash
cd ~/CodingAssistant
git pull --ff-only
python3 tools/m0/opencode_probe.py --model qwen3.5:4b
```

On Windows (PowerShell):

```powershell
py -3 tools/m0/opencode_probe.py --model qwen3.5:9b
```

This invokes `opencode models` and reads Ollama's local model metadata. It does **not** issue a coding assignment, execute model-generated commands, or change source files. OpenCode may update its own discovery cache. The inline per-command configuration pins the endpoint and denies non-Ollama providers at the application-policy level; this is **not** OS security isolation. OpenCode's locally discovered model ID should be `ollama/qwen3.5:4b` or `ollama/qwen3.5:9b`, respectively. An explicit match, model digest, and Ollama-reported `completion` and `tools` capabilities are required. No cloud provider can satisfy the check.

If the probe says `BLOCKED`, run `opencode models` to inspect discovery and share just the Ollama lines and the probe's status. Do not paste secrets or a full credential inventory.

Local evidence is written under `artifacts/m0/opencode-discovery.json`, ignored by Git. Keep this evidence local; do not publish machine details.

After discovery passes, M0 still requires an **OS-isolated** OpenCode coding evaluation, per `docs/SECURITY.md`. The automated benchmark runner must not be run as an ordinary unsandboxed desktop user.

Official sources (checked November 18, 2026):
- OpenCode V2 Ollama auto-discovery: https://opencode.ai/docs/providers
- OpenCode `models` command: https://opencode.ai/docs/cli/commands
- OpenCode provider policies: https://opencode.ai/docs/policies
