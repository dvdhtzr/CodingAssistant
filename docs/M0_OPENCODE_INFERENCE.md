# M0.8b-2b — Actual OpenCode inference wire contract

**Status: CI validation pending; no on-device OpenCode inference authorization. Overall M0 YELLOW.**

## Prior evidence

M0.8b-2a passed on Dell with all 14 safety and discovery checks PASS, container exit code zero, and OpenCode v2.3.0 requesting exactly GET `/api/tags` then POST `/api/show`. That established discovery, not chat/completion inference or worktree safety.

## First gate: offline OpenCode CLI integration test

`.github/workflows/m0-runtime-image.yml` builds the pinned toolchain image and runs `tests/m0_opencode_inference_smoke.mjs` inside Docker with no network or host source mounts. The Node controller binds loopback only and presents deterministic native Ollama discovery metadata plus a **mock OpenAI streaming chat/completions endpoint**. It launches real OpenCode with `run --standalone --model ollama/qwen3.5:4b --title "M0 wire-only session"` and one fixed harmless request. The mock emits only text (`READY`), **never a tool call**. The test requires exactly one pinned streaming chat request, matching discovery, no extra routes, a successful CLI result containing READY, and a bounded process/output budget. No actual local Ollama inference or generated-code execution occurs. The CI mock must pass before any on-device OpenCode run is offered.

This is a wire-compatibility test, not proof that OpenCode respects our security boundaries or that the local model can code. OpenCode permissions are defense-in-depth; OS constraints apply independently.

## Next gate: real model on Dell

Only after the real CLI mock passes: extend the existing private Unix broker to accept a **strictly bounded OpenCode chat request** (not an arbitrary proxy). Enforce a single explicit model/digest, only local upstream `127.0.0.1:11434`, reject unknown providers, methods, paths and model names, stream with byte/time ceilings and no model-returned tools. The actual container is rootless, network-disabled, unprivileged, read-only, without source checkout/host credentials; the only mount is the private AF_UNIX socket directory.

No generated shell/file/Git tool is executed; an unexpected tool call must fail closed. A later separate security gate is mandatory before the benchmark executes model-generated code.

## Evidence boundary

GitHub CI success = the pinned OpenCode *client* can issue and consume the expected mocked streaming inference protocol. Dell M0.8b-2b requires independent real Ollama evidence. Neither alone makes M0 GREEN. M0 qualification still requires the independent three-tasks-by-three-repeats coding benchmarks and safety acceptance.

**CI protocol finding:** The first mock run completed successfully but issued **two** model requests. The pinned OpenCode CLI supports setting the session title before prompting, so the smoke now specifies `--title` and maintains its strict one-inference-call budget. This is an observed run-mode behavior, not a security failure. An unexplained second call must be investigated before allowing the real model through the broker.

## M0.8b-2b real Qwen acceptance candidate (pending CI and Dell)

**The preceding real-CLI mock gate has passed in GitHub CI and was merged as PR #17.** It verified one pinned OpenCode streaming request after supplying an explicit `--title`. It did **not** call Ollama.

This candidate adds `tools/m0/isolated_opencode_real_probe.py` and a distinct `pinned_real_text.py` broker. The new handler **does not broaden** the earlier fixed-tool-call broker. It reuses the already validated exact-model GET `/api/tags` / POST `/api/show` implementation, but accepts exactly one model-pinned `POST /v1/chat/completions` for this fixed CLI prompt:

```text
Reply with the single word READY. Do not use any tools.
```

OpenCode constructs its own system prompts and may advertise coding tools internally. The host broker **does not forward** any of those to Ollama. It validates the exact model, streaming mode and single fixed user prompt, then reconstructs a new upstream local-only request with no tool definitions and strictly bounded output tokens. The Ollama response is buffered with an event/byte/time budget and checked for model identity, a complete terminal event, the expected word and **zero function/tool calls**. Only real returned assistant text—not a synthetic READY response—is normalized into an OpenAI-style SSE response for OpenCode. **This does not prove real-time incremental stream forwarding**, nor does it establish safe coding worktree or autonomous execution.

The isolated Podman process is non-root, network-disabled, has no effective capabilities or new privileges, uses a read-only root and a disposable writable `/tmp` tmpfs with `notmpcopyup`, and mounts only the private AF_UNIX socket directory. The broker refuses a second inference and unrelated model tags/methods. It records only bounded status, routes and model digest, **not prompt bodies or raw model output**. The model does not receive any host files or OpenCode's broad tool catalog.

### Dell command — only after CI and PR merge

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/isolated_opencode_real_probe.py --model qwen3.5:4b
```

Expected if all validations pass: `M0.8b-2b REAL ISOLATED OPENCODE INFERENCE REAL_INFERENCE_PASS: qwen3.5:4b`, all fourteen security/transport checks PASS, container exit 0, pinned model requests = 1 and completed = 1. Local ignored evidence: `artifacts/m0/opencode-real-inference.json`. If BLOCKED, share only the printed statuses/reason. **Never** rerun with public networking, sudo, arbitrary repository mounts, an unpinned model or broader permissions to force success.

This gate is **not** a model qualification or code-writing benchmark. Independent OS-negative/security and model coding task acceptance remain mandatory before M0/M1 can advance.
