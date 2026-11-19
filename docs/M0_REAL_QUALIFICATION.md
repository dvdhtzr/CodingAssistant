# M0.16 — Real local Qwen / OpenCode qualification runner

This stage is the first **actual real-model**, nine-attempt execution of the
three unchanged M0 qualification assignments. M0.13 was a single exact repair;
M0.14 verified nine isolated copies; M0.15 verified that independent offline
API, C# and React acceptance works with known-good and known-bad code.

## Real model behavior — deliberately not hardcoded

`tools/m0/real_qualification.py` runs original
`benchmarks/tasks.json` tasks `01-api-feature`, `02-build-repair` and
`03-fullstack-feature` three times each, starting every attempt from a
fresh verified copy of the original fixture, with original prompts and hashes.

Each run uses real pinned local Qwen inference through the original
OpenCode 2.3.0 CLI and **native `write` tool**; the broker does NOT supply a
known-good answer or modify source itself. It reconstructs the model prompt
from the original task prompt plus current disposable source and exposes only
the native write function for the **exact allowed target files**. A completed
model tool call must identify one allowed file and a bounded, nonempty source
body; an unrecognized path/tool, malformed SSE, mixed prose, wrong model
digest, oversized output, repeated write, additional request or missing
physical OpenCode write is rejected. The broker provides a fixed final message
after the expected tool calls; **that fixed message is not model verification**.

The task-specific surface is:
- API: `/workspace/api/Program.cs` (one model write).
- Repair: `/workspace/api/Program.cs` (one model write).
- Full-stack: `/workspace/api/Program.cs` and
  `/workspace/web/src/TaskSummary.tsx` (two distinct model writes).

Only these files may be written. No model-driven shell, Git, npm, dotnet,
read of other host files, network calls or other tools are permitted.
The broker reads only the **approved** input files for the selected task.
OpenCode itself executes with restricted permissions, and the entire
process tree runs in **rootless**, `--network=none`, `--read-only`,
`--cap-drop=all`, no-new-privileges Podman. The only mounts are a
disposable workspace copy and a private, restricted Unix model socket.
All original fixtures and unrelated repos are unmounted.

After the model stage ends, the separate M0.15 evaluator (a **different
container with NO model socket**) performs trusted, fixed .NET build/behavior
checks and React typecheck/Vitest against the unchanged independent
acceptance tests. Generated code is never executed on the regular host.
A failed model answer/build/UI test scores **MODEL_FAILED**, not a fabricated
pass. Missing safety evidence or sandbox failures stop the batch.

Outputs under ignored
`artifacts/m0/evaluation-<UTC>-<ID>/results.json` include the original
manifest/model/image digests, per-attempt hashes, provider call counts,
native-write paths and SHA-256, relevant bounded OpenCode output,
independent functional checks, and the final workspace copies for manual
review. The benchmark does NOT set safety fields to false: the original
`unauthorized_operation`, `false_verification_claim`, and
`unrecoverable_execution_failure` fields remain **null** pending an
independent safety review. Consequently `qualify.py` must return
NOT_QUALIFIED until the review is separately completed even if 9/9 code
builds and passes behavior checks. Qualification still requires >=2/3 per
task, >=7/9 total and zero critical safety/recovery failures.

## Operator steps (Dell, only after CI green and merge)

1. Build a separate trusted qualification evaluation image with a single,
   **explicit** build operation. This may access package registries to
   provision original React dependencies; no inference or untrusted code
   runs during image provisioning:

   ```bash
   cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/real_qualification.py --prepare-evaluator
   ```

   The runner inspects and prints the full immutable image IDs. A
   mutable tag is never used for sandbox execution. The generated npm
   lockfile/hash is included inside that immutable image, but cross-build
   reproducibility is not yet proven. No cloud AI or Azure services are used.

2. Run the **same real model/Ollama bridge/code evaluator** in a one-task
   diagnostic first, to verify that actual `write` tool calls work on the
   Dell. This is intentionally NOT the nine-run qualification:

   ```bash
   cd ~/CodingAssistant && python3 tools/m0/real_qualification.py --model qwen3.5:4b --diagnostic-task 02-build-repair
   ```

   If the model fails to generate working code, that is still useful
   evidence; do not weaken the sandbox or substitute known-good code.

3. Once the real-model native tool path has been observed without an
   infrastructure/security blocker, run **all nine genuine model tasks**:

   ```bash
   cd ~/CodingAssistant && python3 tools/m0/real_qualification.py --model qwen3.5:4b
   ```

   The script records all attempts and summarizes verified functional
   success. A completed batch is evidence, **not M0 GREEN** until
   independent security review and frozen qualification thresholds pass.

## CI scope

CI uses deterministic mocked Qwen answers **only to exercise actual native
OpenCode `write` tool execution and independent offline acceptance**, for
each of the original three task shapes, plus unit tests for frozen prompts,
safe paths, strict streaming, source integrity, tool budgets, isolation
commands and failure handling. CI never claims real Qwen performance.

Do not run the legacy unisolated `benchmark.py` on the Dell. No unrelated
repositories, third-party skills, system credentials or Git remotes are
mounted or modified.
