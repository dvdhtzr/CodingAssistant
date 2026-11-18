# CodingAssistant

Cross-platform, **local-first** autonomous software engineering assistant. OpenClaw orchestrates; OpenCode does coding; Ollama serves local Qwen models. The approved target is a .NET 10 Minimal API plus React/TypeScript Mission Control, SQLite, SignalR, and Git worktrees.

> **Current status: M0 IN PROGRESS (not GREEN).** This repository currently contains the model/hardware feasibility and evaluation foundation, not a running autonomous developer. No model or platform has qualified.

## Quick start: safe checks

Requires Python 3.10+ and a running Ollama instance. These checks do not install software or launch an autonomous coding agent.

On Linux:
```bash
python3 -m unittest discover -s tests -v
python3 tools/m0/preflight.py --model qwen3.5:4b
python3 tools/m0/inference_probe.py --model qwen3.5:4b
```

On Windows (PowerShell):
```powershell
py -3 -m unittest discover -s tests -v
py -3 tools/m0/preflight.py --model qwen3.5:9b
py -3 tools/m0/inference_probe.py --model qwen3.5:9b
```

Local results appear under `artifacts/m0/` (ignored by Git). Don't commit machine inventories, model execution logs, secrets, or credentials.

## Roadmap

| Milestone | Description | Status |
| --- | --- | --- |
| M0 | Host inventory, Ollama/OpenCode model feasibility, repeatable 3×3 benchmarks | IN PROGRESS |
| M1 | OpenClaw → ACP → OpenCode, externally enforced security boundary and approvals | NOT STARTED |
| M2 | ASP.NET Core API, auth, SQLite, state machine, reconciliation | NOT STARTED |
| M3 | React Mission Control, SignalR, supervision and evidence UI | NOT STARTED |

## M0 tools

- `tools/m0/preflight.py`: read-only host and Ollama inventory
- `tools/m0/inference_probe.py`: one local tool-call probe
- `tools/m0/benchmark.py`: **opt-in** three-task, three-repeat OpenCode runner
- `tools/m0/qualify.py`: evidence-based primary-model acceptance
- `benchmarks/fixtures/`: disposable .NET and React source inputs
- `benchmarks/acceptance/`: independent acceptance verification
- `docs/`: architecture progress, security and operations

**Warning:** The M0 benchmark has no built-in OS-enforced sandbox. It must only run in a separately isolated disposable environment. `--acknowledge-unisolated-m0-risk` acknowledges this; it does not make execution safe. See [security guidance](docs/SECURITY.md).

## Approved constraints

One active managed task initially; plan approval bound to version/hash; untrusted coding only after independent OS-enforced restrictions; evidence-based verification; no cloud AI requirement. Windows and Linux must pass their own acceptance gates before claiming support. GitHub operations for development of this repository may be performed under the owner's Nov 18, 2026 authorization; the **product's** future autonomous agents still require scope-bound approval for remote Git operations.

## Next Dell check — OpenCode model discovery

After the initial Dell prerequisites and Ollama tool-call probe pass, update your clone and run:

```bash
cd ~/CodingAssistant
git pull --ff-only
python3 tools/m0/opencode_probe.py --model qwen3.5:4b
```

The probe only checks whether OpenCode can **discover** your locally installed Qwen model. It does not ask an agent to write code, and it does not turn M0 GREEN. See [M0.3 discovery instructions](docs/M0_OPENCODE_DISCOVERY.md).

## M0.4 — rootless isolation preflight (Dell)

Model discovery passed on the Dell (owner-reported). **Before running any model-generated code**, [check Podman rootless isolation](docs/M0_ISOLATION.md). This is a negative-test preflight, not full M1 security authorization.

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/sandbox_probe.py
```

If Podman or its container image is not yet installed, follow the one-time setup instructions in the linked document. No agent executes during this test.
