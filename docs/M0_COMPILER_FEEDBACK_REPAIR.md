# M0.17 — Bounded compile-feedback repair in the real Qwen/OpenCode qualification

**Observed owner benchmark evidence (2026-11-19, retained as-is):**
`artifacts/m0/evaluation-20261119T152341Z-6aa13714/results.json`
reported 6/9 verified: all three C# compiler-repair attempts and all
three full-stack attempts passed, but 0/3 API feature attempts built.
Each failed API generation used `[FromQuery]` without a
`Microsoft.AspNetCore.Mvc` namespace import. Qwen/OpenCode completed
each native write; the independent evaluator's `offline_restore` was
true but `offline_build` was false. The model was never given compiler
feedback. The original result is **NOT_QUALIFIED** and must not be altered.

## Real behavior added

An independent, network-disabled, non-root, read-only-root .NET evaluator
now emits **at most five bounded printable C# compiler error messages**
(`CSdddd: explanation`), not arbitrary build logs, generated source,
test output, or host paths. The host validates their exact schema before
using them in any model request. The evaluator itself runs without the
local Ollama socket; no untrusted model is permitted to execute a shell.

If and only if the **first model native write was completed correctly**,
the *independent* `dotnet restore` passed, the *independent*
`dotnet build` failed with actual validated C# diagnostics, and
all OS/workspace security gates passed, single-file tasks
`01-api-feature` and `02-build-repair` receive **one bounded
compiler-guided correction** within that same original attempt.
Full-stack tasks do not get a retry yet; their two-file changes
require a separate, rigorously scoped design.

The retry uses the **same real Qwen model**, original frozen task prompt,
current disposable C# project contents, a small validated compiler error
summary, and the same native OpenCode `write` tool. It runs in a **new**
private broker socket and rootless, no-network, capability-free Podman
container with only the original approved writable source path. The
model is asked to write the complete corrected file. Neither the broker
nor the runner silently injects `using` directives or prebuilt fixes.
The file is then rebuilt and behavior-tested in **another independent
no-model container**. The complete tool/evaluator traces, before/after
source hashes and initial compiler diagnostics are saved.

Compiler-repair attempts are **not extra benchmark samples**, and are
never repeatedly retried. An initial build failure counts as a valid
failed model result if the genuine corrective write fails or its
independent acceptance still fails. Any security/transport/integrity
evidence violation remains blocking, never turned into model success.

## CI and trust boundaries

Model-free GitHub CI reproduces the owner's missing `[FromQuery]`
namespace error as `CS0246`, independently confirms that adding the
necessary namespace makes the exact API behavior pass, and checks the
compiler feedback's bounded schema. A separate CI smoke uses a real
native OpenCode write with a **deterministic MOCK model** that receives
the captured error and supplies corrected C#; the independent
offline evaluator verifies the repair. This proves the execution path,
**not** real Qwen competence.

The original frozen source/prompts, the older 6/9 result and the
qualification thresholds (>=2/3 per task, >=7/9 overall, zero critical
security errors and independent review) remain unchanged. Safety audit
fields stay null pending actual independent review.

**Operator, after PR green and merged:** No need to rebuild the M0.15
evaluator image: only the host-supplied fixed evaluation script changes.
Run an isolated real model diagnostic first:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/real_qualification.py --model qwen3.5:4b --diagnostic-task 01-api-feature
```

The single diagnostic does not qualify M0. After validating genuine
compiler error capture and native model-driven repair on the Dell,
run a **new** frozen 3×3 with the same
`python3 tools/m0/real_qualification.py --model qwen3.5:4b` command.
Do not overwrite the historical results or run unisolated tools.

**Overall M0 remains YELLOW** until a fresh 3×3 passes and the
independent safety review is complete.
