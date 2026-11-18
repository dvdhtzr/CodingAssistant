# Kubuntu / Ubuntu — Dell setup and M0 validation

Commands in this document are instructions for the owner; ChatGPT has not executed them on the Dell.

## Read-only inventory

From the repository root, run:
```bash
cat /etc/os-release
python3 --version
git --version
dotnet --version
node --version
npm --version
ollama --version
ollama list
python3 -m unittest discover -s tests -v
python3 tools/m0/preflight.py --model qwen3.5:4b
```

A missing executable reports `command not found`; that's useful diagnostic information. Inventory is written to ignored `artifacts/m0/inventory.json`; do not commit it.

## If .NET 10 SDK is absent

First check the package candidate (read only):
```bash
apt-cache policy dotnet-sdk-10.0
```
If a compatible candidate is available:
```bash
sudo apt update
sudo apt install dotnet-sdk-10.0
dotnet --version
```
These commands modify system packages and require your Linux password. If no candidate exists, consult Microsoft documentation for the exact installed Ubuntu/Kubuntu release; do not add arbitrary outdated repositories.

## OpenCode

Consult the official OpenCode release and installation docs for the current supported Linux procedure. Inspect scripts before executing them. Confirm `opencode --version` and supported `run` arguments. OpenClaw isn't needed for M0 inventory/probes; defer Gateway services to M1.

## Ollama

Once your Qwen download finishes:
```bash
ollama list
ollama run qwen3.5:4b 'Give a one sentence greeting.'
python3 tools/m0/inference_probe.py --model qwen3.5:4b
```
The probe sends one tool-call request to Ollama and writes ignored evidence. It does not run shell commands on behalf of the model.

## Node / npm

Check `node --version` and `npm --version`. OpenClaw later needs a currently supported and patched Node version. Confirm against its documentation; distribution defaults may be too old.

## Stop before benchmark

Do not run `benchmark.py` as a normal, privileged or credential-bearing desktop user. It executes agent-generated shell commands and code. Read `docs/SECURITY.md`; only run in a verified disposable isolated environment.
 
## OpenCode model discovery (M0.3)

Once the other checks are green:

```bash
cd ~/CodingAssistant
git pull --ff-only
python3 tools/m0/opencode_probe.py --model qwen3.5:4b
```

Expected: `M0.3 DISCOVERY PASS` and five `PASS` checks. The probe only runs `opencode models` and reads Ollama's local metadata; it doesn't execute agent code. Its evidence remains under ignored `artifacts/m0/`. If blocked, inspect `opencode models` and share only its `ollama/` lines.
