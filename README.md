# CodingAssistant

Cross-platform, **local-first** autonomous software engineering assistant. OpenClaw orchestrates; OpenCode does coding; Ollama serves local Qwen models. The approved target is a .NET 10 Minimal API plus React/TypeScript Mission Control, SQLite, SignalR, and Git worktrees.

> **Current status: M0 IN PROGRESS (not GREEN).** This repository currently contains the model/hardware feasibility and evaluation foundation, not a running autonomous developer. No model or platform has qualified.

## Try the interactive MVP today

**First usable vertical slice:** choose one source file and a task, get a proposal from your installed **local Ollama** model, inspect the diff, and explicitly type `APPLY` to save it **only in a disposable copy**. There is no automatic modification of your original project, no autonomous tool execution, and no network service beyond `127.0.0.1:11434`.

On the Dell with Ollama running:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/mvp/assist.py --demo --build
```

The built-in demo asks Qwen 4B to fix the frozen C# compile-error fixture. **It shows the actual proposed diff first.** Type `APPLY` only if the change looks correct. The optional `--build` uses the existing immutable local M0.7 toolchain image in a rootless network-disabled Podman container for fixed .NET restore/build; for this demo it also runs the repaired app. A successful demo should print `Total: 15`. If Qwen returns an unusable suggestion, reject it; do not force success or modify the fixture.

### Automatic compiler- and acceptance-feedback correction (interactive)

When `--build` is requested for a C# file, a failed isolated **C# compilation** triggers a correction. With explicit `--run`, a compiled program that exits nonzero and emits recognizable `FAIL:` acceptance assertions can also trigger a correction. Up to **two automatic correction proposals** are permitted per session. Each proposal shows a new diff and requires another exact `APPLY` response. Corrections build on the *last approved disposable copy*, never the original or a failed copy in place. A successful build stops the loop; repeated compiler diagnostics or the two-correction limit also stop it.

For example:

```bash
python3 tools/mvp/assist.py --demo --build
```

If restore, the container image, Podman, or app execution fails without actual compiler diagnostics or recognizable failed-test assertions, the tool stops rather than guessing a repair. Each approved copy keeps its `change.patch` plus `result.json` with its parent run, model and model digest, source hashes, compiler diagnostics, failed-test assertions, and verification outcome. All history remains under ignored `artifacts/mvp/`. No model-generated command is ever run on the host. This is a controlled interactive feature, **not autonomous model qualification**.

### Manual retry of a previous run (still supported)

If a previously approved C# change fails to compile **or fails executable acceptance**, keep its run ID and run:

```bash
python3 tools/mvp/assist.py --retry <RUN_ID> --file Program.cs --run
```

The CLI rechecks the **existing disposable copy** inside rootless, offline Podman, supplies bounded compiler errors or genuine failed acceptance assertions to your local model, shows a NEW diff, and asks you to type `APPLY` again. For a failed acceptance run, use `--file <source-file>` and `--run`; optionally give `--task` to restate the original business objective. The pinned local model identity is preserved. It writes the new change only into **another** disposable copy; both the original project and the prior failed copy remain unchanged. After a successful build, `--run` runs the repaired sample inside the same isolated environment. A missing container image or other infrastructure failure stops the retry rather than giving Qwen invented compiler errors.

## Next challenge — real order-pricing business logic

The `benchmarks/fixtures/04-order-pricing/` project contains a small, multi-class
.NET 10 application with a defect that **compiles successfully** but computes
discounted sales tax incorrectly. Its fixed executable acceptance harness runs
11 independent checks, including a half-cent boundary that prevents rounding
taxable and exempt discounts separately. On the Dell, run:

```bash
python3 tools/mvp/assist.py --project benchmarks/fixtures/04-order-pricing --file PricingCalculator.cs --task "Fix SAVE10 tax: discount all merchandise, then tax only taxable items after their proportional discount; preserve cent rounding, shipping exclusions, signatures and validation." --model qwen3.5:4b --run
```

Review each proposed diff and type `APPLY` only if you approve it. The original
project and its acceptance oracle remain unchanged. `--run` uses the same fixed
rootless, network-disabled Podman restore/build/run commands as the demo.
The result must include `ORDER-PRICING ACCEPTANCE PASS` and
`11/11 checks passed`. If acceptance fails despite successful compilation, the tool captures bounded
failed assertions and offers up to two corrective proposals, **each needing a
new APPLY**. Non-test runtime failures remain blocked. See the fixture README.

#### Resume the failed order-pricing run

To retry the first real Qwen attempt against its historical frozen copy,
which compiled but passed only 5/10 checks:

```bash
python3 tools/mvp/assist.py --retry 20261119T032101Z-1cb9e2cf --file PricingCalculator.cs --task "Preserve the original pre-discount Subtotal, apply SAVE10 once to total merchandise, then compute tax on taxable merchandise after its share of the discount. Keep validation, shipping and rounding behavior." --model qwen3.5:4b --run
```

This reuses the **failed approved copy** as input, rereads live acceptance
failures, and asks for a new diff. The failed copy and the frozen fixture remain
unchanged. The 5/10 result is a real model outcome, not a qualification pass.

For your own project (one source file at a time):

```bash
python3 tools/mvp/assist.py --project /path/to/project --file Program.cs --task "Fix the null handling"
```

The workflow copies selected non-secret source/project files into `artifacts/mvp/<run>/project/` (Git ignored), writes the approved replacement there, and saves `change.patch` alongside it. It does not copy Git history, `.env`, `appsettings.json`, dependencies, build outputs or symlinks. Only the explicitly selected file content and task are sent to Ollama. Add `--build` for a Linux-only, fixed offline .NET compile check when a single top-level `.csproj` is present; add `--run` to also execute that project's approved disposable copy inside the same restricted Podman container. No code is merged, committed, or pushed.

**Deliberate shortcut:** this interactive MVP's proposal stage uses Ollama's local OpenAI-compatible API **directly**; it does not start an OpenCode agent. The already validated isolated OpenCode work remains in `tools/m0/`, and the later autonomous workflow will reintegrate it. This first command is meant to produce a useful, reviewable edit now—not claim M0 qualification or production-grade autonomous coding.

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

## M0.5 — allowlisted local Ollama bridge feasibility

The Dell passed M0.4's seven rootless Podman isolation checks (owner-provided evidence). Next, use the [M0.5 local inference IPC preflight](docs/M0_OLLAMA_BRIDGE.md) to prove a network-disabled container can reach **only** a local, read-only Ollama metadata broker via a private Unix socket. This still doesn't run OpenCode or generated code:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/bridge_probe.py
```

**M0 remains YELLOW.** A passing broker preflight is not an approved autonomous coding run.

## M0.6 — pinned inference inside the isolated Dell container

M0.5's seven broker checks and loopback were **reported PASS on the Dell**. The next step is a single bounded, model-pinned Ollama inference call over container-local HTTP → private Unix socket; it is **not** OpenCode execution and does not run AI-generated code. See [M0.6 instructions](docs/M0_PINNED_INFERENCE.md).

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/inference_transport_probe.py --model qwen3.5:4b
```

M0 remains **YELLOW** until independent safe coding-evaluation evidence is available.

## M0.7 — offline development toolchain image (Dell)

M0.6 **isolated, model-pinned inference PASS** was reported on the Dell with all eight checks. Next, verify that OpenCode, .NET 10, Node and Git can **start without networking** inside an isolated container. The toolchain build intentionally downloads official dependencies, then executes its checks with `--network=none`. No coding agent or model call occurs.

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/runtime_image_probe.py --build
```

Details and expected results: [M0.7 runtime image](docs/M0_OFFLINE_RUNTIME.md). **M0 remains YELLOW** pending full safe coding and model qualification.
