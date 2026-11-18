# M0.8b-2b — Actual OpenCode inference wire contract

**Status: CI validation pending; no on-device OpenCode inference authorization. Overall M0 YELLOW.**

## Prior evidence

M0.8b-2a passed on Dell with all 14 safety and discovery checks PASS, container exit code zero, and OpenCode v2.3.0 requesting exactly GET `/api/tags` then POST `/api/show`. That established discovery, not chat/completion inference or worktree safety.

## First gate: offline OpenCode CLI integration test

`.github/workflows/m0-runtime-image.yml` builds the pinned toolchain image and runs `tests/m0_opencode_inference_smoke.mjs` inside Docker with no network or host source mounts. The Node controller binds loopback only and presents deterministic native Ollama discovery metadata plus a **mock OpenAI streaming chat/completions endpoint**. It launches real OpenCode with `run --standalone --model ollama/qwen3.5:4b` and one fixed harmless request. The mock emits only text (`READY`), **never a tool call**. The test requires exactly one pinned streaming chat request, matching discovery, no extra routes, a successful CLI result containing READY, and a bounded process/output budget. No actual local Ollama inference or generated-code execution occurs. The CI mock must pass before any on-device OpenCode run is offered.

This is a wire-compatibility test, not proof that OpenCode respects our security boundaries or that the local model can code. OpenCode permissions are defense-in-depth; OS constraints apply independently.

## Next gate: real model on Dell

Only after the real CLI mock passes: extend the existing private Unix broker to accept a **strictly bounded OpenCode chat request** (not an arbitrary proxy). Enforce a single explicit model/digest, only local upstream `127.0.0.1:11434`, reject unknown providers, methods, paths and model names, stream with byte/time ceilings and no model-returned tools. The actual container is rootless, network-disabled, unprivileged, read-only, without source checkout/host credentials; the only mount is the private AF_UNIX socket directory.

No generated shell/file/Git tool is executed; an unexpected tool call must fail closed. A later separate security gate is mandatory before the benchmark executes model-generated code.

## Evidence boundary

GitHub CI success = the pinned OpenCode *client* can issue and consume the expected mocked streaming inference protocol. Dell M0.8b-2b requires independent real Ollama evidence. Neither alone makes M0 GREEN. M0 qualification still requires the independent three-tasks-by-three-repeats coding benchmarks and safety acceptance.
