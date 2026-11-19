# CodingAssistant

Cross-platform, **local-first** autonomous software engineering assistant. OpenClaw orchestrates; OpenCode does coding; Ollama serves local Qwen models. The approved target is a .NET 10 Minimal API plus React/TypeScript Mission Control, SQLite, SignalR, and Git worktrees.

> **Current status (Nov 19, 2026): M0 GREEN on Dell at the independently reviewed evidence-assurance level.** The local `qwen3.5:4b` / OpenCode runner completed **9/9** frozen coding attempts, followed by M0.18 independent review and nine separate offline acceptance rechecks: **EVIDENCE_REVIEW_PASS / QUALIFIED**. This is *not* a production-grade autonomous developer, a cryptographic historical attestation, or a Windows-host security qualification. **M1 secure orchestration is now in read-only discovery; autonomous execution remains gated.**

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

### Handling slow local Ollama completions

A Dell CPU may take longer than the earlier fixed 300-second socket timeout to
generate a **complete C# replacement**. The CLI now uses a 600-second default
**socket inactivity** limit for the completion request and accepts
`--inference-timeout 900` (valid range 30–1200 seconds) when the model is slow.
A timeout during the model catalog request is labeled separately. This is not a
total wall-clock deadline and is not proof the model is unhealthy.

For the stronger eleven-check order-pricing benchmark, use:

```bash
python3 tools/mvp/assist.py --project benchmarks/fixtures/04-order-pricing --file PricingCalculator.cs --task "Fix SAVE10 tax: apply the coupon to all merchandise once, round the order-wide discount once, tax only taxable merchandise after its proportional discount, and preserve subtotal, shipping, validation and public signatures." --model qwen3.5:4b --inference-timeout 900 --run
```

If the local server stalls, `ollama ps` shows currently loaded models, and
`ollama list` shows installed models. The CLI never applies an incomplete
response; it prints a phase-specific timeout message and leaves the
unmodified original and previously approved disposable copies intact.
No background retries, no remote-model fallback, and no changed Podman privileges.

### Focused compiler correction after a multi-file edit

A real Dell run completed the two-file checkout challenge with **9/9 passing
acceptance checks** after one failed C# build. Qwen's first
`DiscountPolicy.cs` proposal appended the exact non-code line
`--- END ---`, triggering `CS1022` and `CS0116`.
Both classes were unnecessarily regenerated in the correction round.

The assistant now removes **only that exact terminal marker**, and only when
it follows a closing brace in a C# model response. The cleanup is reported,
not hidden. No general rewrite, arbitrary Markdown stripping, source changes
to originals, or automatic approval is introduced.

If every actual compiler diagnostic names only a subset of the owner-selected
C# files, subsequent automatic corrections request replacements **only for
those implicated files**. Other previously approved classes are copied
unchanged into the next disposable workspace. One new diff and explicit
`APPLY` remain mandatory, and the **entire project** is built and tested again.
If compiler diagnostics refer to unknown/multiple files, all selected files
remain eligible; acceptance-test failures also retry the full selected set.
Saved `--retry` runs retain the original allowed file list but record precisely
which files were re-proposed on each correction.

These targeted changes address the actual **103.6-second unnecessary second
inference** seen in the successful Dell run. They are optimizations, not a
guarantee of a particular runtime or model accuracy.

### Faster and diagnosable local Qwen inference

A real two-file Dell attempt completed its `DiscountPolicy.cs` response but
then spent a long time on `CheckoutCalculator.cs` before returning
`Model returned incomplete output or attempted a tool call`. **No diff was
approved or staged.** This can happen when a thinking model exhausts its
completion-token budget; the old error did not expose the reason.

For **multi-file mode only**, the default Ollama OpenAI-compatible request now
uses `reasoning_effort: "none"` to suppress long internal thinking and
prioritize returning the full source file. You can choose
`--reasoning-effort auto` to restore the model's original thinking behavior.
Single-file mode keeps its previous default unless explicitly overridden.

The CLI also reports per-call generation elapsed time, completion-token usage
when Ollama supplies it, and distinguishes `finish_reason=length` from an
unapproved tool call or other unfinished output. **Truncated output is always
rejected**, and a failed second proposal never partially stages the first.

The latest multi-file checkout retry command is:

```bash
cd ~/CodingAssistant && git switch main && git pull --ff-only && python3 tools/mvp/assist.py --project benchmarks/fixtures/05-multifile-checkout --files DiscountPolicy.cs CheckoutCalculator.cs --task "Support MEMBER20 for members only, retain SAVE10 for everyone, apply discounts once to all merchandise with order-level rounding, and tax only taxable items after their proportional discount; preserve signatures and validations." --model qwen3.5:4b --reasoning-effort none --inference-timeout 900 --run
```

This reduces thinking overhead but may change model quality; the acceptance
checks remain the arbiter. It does not change security boundaries or proof
of qualification; a local Dell run must demonstrate success.

## Next milestone — bounded repository-aware multi-file editing

The MVP now has an explicit **2–3 file** mode (`--files`) for small C# changes
that genuinely span classes. The owner names every editable source file. For
each selected file, the local model receives its full current contents plus
bounded read-only neighboring C# context (including earlier proposals in the
same batch). Unselected neighbor files are never edited; the executable
`Program.cs` acceptance harness is excluded from automatic neighbor discovery.

On the Dell, use the new two-file checkout benchmark:

```bash
cd ~/CodingAssistant && git switch main && git pull --ff-only && python3 tools/mvp/assist.py --project benchmarks/fixtures/05-multifile-checkout --files DiscountPolicy.cs CheckoutCalculator.cs --task "Support MEMBER20 only for members, retain SAVE10 for everyone, apply coupons once to total merchandise and round the order-wide discount once, then tax only taxable merchandise after its proportional discount. Preserve validation, shipping exclusions and public signatures. Repair both selected classes." --model qwen3.5:4b --inference-timeout 900 --run
```

The separate model proposals appear together as **one combined diff**. The
entire batch needs one explicit `APPLY` before any file is edited, and edits
are made together only to a **new disposable project copy**. If the second
model call times out or you reject the combined diff, no batch is staged.
The same fixed rootless/no-network Podman build and executable acceptance
verification is used. Build or recognizable test failures can trigger at most
two additional batch proposals with fresh approval each time. A saved
multi-file run can be resumed via `--retry RUN_ID --model ... --run`.

`05-multifile-checkout` contains two deliberate defects in separate classes:
missing member-only `MEMBER20` support and tax incorrectly computed before a
coupon. Its independent executable contract has **9 checks**.
Deterministic GitHub CI proves that the frozen fixture fails and a known-good
two-file correction passes 9/9. This is **not** proof that Qwen will pass it
until the real Dell run is observed.

**Current limits:** only existing owner-selected source files, 2–3 files per
batch, no new-file generation, no Git commits/merges, no general autonomous
file selection. Single-file `--file` and all earlier behavior remain available.

For your own project (one source file at a time):

```bash
python3 tools/mvp/assist.py --project /path/to/project --file Program.cs --task "Fix the null handling"
```

The workflow copies selected non-secret source/project files into `artifacts/mvp/<run>/project/` (Git ignored), writes the approved replacement there, and saves `change.patch` alongside it. It does not copy Git history, `.env`, `appsettings.json`, dependencies, build outputs or symlinks. Only the explicitly selected file content and task are sent to Ollama. Add `--build` for a Linux-only, fixed offline .NET compile check when a single top-level `.csproj` is present; add `--run` to also execute that project's approved disposable copy inside the same restricted Podman container. No code is merged, committed, or pushed.

**Deliberate shortcut:** this interactive MVP's proposal stage uses Ollama's local OpenAI-compatible API **directly**; it does not start an OpenCode agent. The already validated isolated OpenCode work remains in `tools/m0/`, and the later autonomous workflow will reintegrate it. This first command is meant to produce a useful, reviewable edit now—not claim M0 qualification or production-grade autonomous coding.

## M0 qualification exit / M1 read-only kickoff

The frozen M0 model benchmark on the Dell reached **9/9 verified**.
Independent M0.18 review reported nine fresh isolated evaluator rechecks
`EVIDENCE_PASS` and **QUALIFIED**. The benchmark and append-only audit
sidecars remain local (Git-ignored). For the exact review limitations see
[the independent audit contract](docs/M0_INDEPENDENT_EVIDENCE_AUDIT.md)
and [current M0 status](docs/M0_STATUS.md).

**M1 does not yet authorize autonomous coding.** First measure the local
Gateway and Node compatibility without installing or starting anything:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/discovery.py
```

The current OpenClaw Gateway needs patched Node 24/26; the Dell's
previously reported Node 22 is not compatible. A `PREREQUISITES_BLOCKED`
report is expected and safe; we will decide whether to use a separate
rootless Gateway runtime rather than modifying the working M0 toolchain.
See [M1 read-only discovery](docs/M1_GATEWAY_DISCOVERY.md).

### M1.0 Dell findings and M1.1 isolated Gateway preflight

The Dell's version-only discovery found host Node `22.22.1` (below
OpenClaw's supported runtime), no host OpenClaw, and working Podman
`5.7.0`. We **do not** replace host Node or install OpenClaw globally.
The next M1 check uses the official pinned Gateway image **only** for
an isolated, network-disabled runtime/version check:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/gateway_image_probe.py --prepare
```

The explicit `--prepare` downloads one versioned official GHCR image
into the rootless user image store, not the host's Node installation.
No Gateway, ACP, Ollama or coding session runs. See
[M1 discovery and safety boundaries](docs/M1_GATEWAY_DISCOVERY.md).

### M1.1 verified official Gateway runtime / M1.2 next check

Dell M1.1 passed with OpenClaw `2026.11.5` and patched Node
`24.22.1` inside rootless Podman; host Node 22 and host OpenCode
were unchanged. The next gate **temporarily starts a real Gateway**
in a no-network, no-mount, loopback-only container and verifies
its documented HTTP health endpoints, then destroys the guest:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/gateway_loopback_probe.py
```

No ACP agent, model, cloud AI, Git operation or host source is involved.
This is not yet a production Gateway, an approved coding workflow or
Windows M1 qualification. See `docs/M1_GATEWAY_DISCOVERY.md`.

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
| M0 | Host inventory, Ollama/OpenCode model feasibility, repeatable 3×3 benchmarks | GREEN on Dell; Windows separate gate |
| M1 | OpenClaw → ACP → OpenCode, externally enforced security boundary and approvals | M1.0 discovery only |
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
