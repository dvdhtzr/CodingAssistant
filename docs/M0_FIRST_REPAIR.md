# M0.11 — First real Qwen repair pilot (constrained, not autonomous)

**Status: PENDING GitHub CI and Dell acceptance. Overall M0 YELLOW.**

## Rationale

The owner reported M0.10 NEGATIVE_PRECHECK_PASS on Dell on November 18. Its fixed adversarial preflight accepted all 18 checks. OpenCode's real pinned Qwen text inference, isolated writable workspace and child-process isolation have also passed. These are necessary but not sufficient for unrestricted autonomous coding.

The approved first coding task is the frozen 02-build-repair C# console fixture. Program.cs contains a compile error, and independent functional acceptance requires stdout to equal **Total: 15**.

## Stage 1 — model-authored proposal only

- Actual pinned OpenCode 2.3.0 receives the broken Program.cs and the fixed task goal. Shell, editing and writing tools are denied. Only the private AF_UNIX socket directory is mounted. The container has no public network, host project, repository, credentials or privileged identity.
- The host broker permits a single exact pinned inference request and reconstructs the prompt independently from the frozen source. It discards OpenCode tool definitions and other context, requires valid streamed text and stable model digest, and refuses tool calls or second requests.
- The proposed full Program.cs must match the original except for **one exact permitted method-name repair**. This is an intentionally strict pilot: it verifies a specific model-proposed change, not general coding ability. Unknown extra statements, unsafe modifications or non-source replies fail closed.
- The accepted proposed source is retained temporarily in memory. Evidence contains hashes and statuses, never raw source or model response.

## Stage 2 — trusted, fixed offline build

- The controller stages only the two frozen source files in a new temporary workspace. It writes the validated candidate to the temporary Program.cs and leaves Repair.csproj unchanged.
- A separate non-root, rootless Podman container runs a **fixed** offline dotnet restore (using an empty local NuGet source), dotnet build and the resulting DLL. Only the disposable copy is writable; no broker socket, host home or source checkout is mounted. The root filesystem is read-only, effective capabilities are dropped and outbound networking is denied.
- Success requires an offline build, exactly **Total: 15** as application output, all evaluator isolation checks, unchanged original fixture, unmodified project file and intact host sentinel. Workspaces are deleted after completion. Per-stage container ID evidence permits forced cleanup after timeouts.

## What this does NOT prove

This is **not** an OpenCode autonomous tool-use session: no model-generated shell/file command is ever executed. It is also not one of the three task x three repeat benchmark qualification runs and does not satisfy the independent M1 ten-test security gate. The original unisolated benchmark runner remains disabled for normal workstations.

## Operator command — ONLY after CI green and PR merge

    cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/controlled_repair_pilot.py --model qwen3.5:4b

A genuine pass reports:

    M0.11 FIRST CONTROLLED REPAIR CONTROLLED_REPAIR_PASS: qwen3.5:4b

with the proposal and evaluator checks all PASS, exactly one pinned model response and a successful independent C# execution.

Ignored local evidence: artifacts/m0/controlled-repair.json. If BLOCKED, share the printed statuses and reason. Do not add public network access, mutable images, source repository mounts or model-executed tools to force success.

GitHub CI checks model-free strict broker/test contracts, and independently compiles a **static, known-good** replacement in the same offline evaluator image. The static CI smoke is not real model evidence. The Dell acceptance is a separate required gate.
