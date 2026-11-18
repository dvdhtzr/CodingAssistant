# M0.8b-1 — Fixed OpenAI stream across isolated private transport

**Status: PENDING Dell validation. Overall M0 remains YELLOW.**

## Purpose and scope

M0.8a has passed on Dell: qwen3.5:4b returned one structured tool call using Ollama's local OpenAI-compatible streaming API. M0.6 previously proved a private Unix socket can connect a network-disabled Python container to the host's Ollama with a fixed native-chat prompt. M0.7 verified the offline .NET/Node/OpenCode toolchain image. This checkpoint combines those **transport** observations using the existing M0.7 image and Node 22, while keeping the agent completely disabled.

The proof uses a deliberately narrow path:

1. The host starts a private AF_UNIX broker in a mode-0700 temporary directory.
2. Rootless Podman starts the **existing immutable-ID** M0.7 toolchain image with no public network, read-only rootfs, no capabilities, no-new-privileges, user-namespace isolation, and notmpcopyup on its disposable /tmp tmpfs.
3. The only mount is the private socket directory at /run/codingassistant:ro. There are **no project, home or source-code mounts**.
4. A fixed, reviewed Node script binds only 127.0.0.1 *inside the container* and forwards one allowlisted OpenAI request through the Unix socket to a host broker.
5. The host broker rejects arbitrary prompts, models, tool definitions, paths, HTTP methods, and request options; it independently reconstructs the **single** fixed report_ready(status=READY) request and contacts only host 127.0.0.1:11434.
6. The broker checks the model digest before and after and parses the provider's bounded SSE stream. It emits a **synthetic, normalized, fixed SSE response** only after validating the model's single harmless tool call. No model-generated function or code is executed. Note: buffering before replay is **not** proof of real-time incremental OpenCode streaming.
7. The container tests the pinned /v1/models response and the known report_ready stream, denies a foreign model and unsupported path, and checks root mount options, non-root identity, hidden host sentinel, and outbound TCP denial.

The model is asked for one inert function call. **No OpenCode session is started and no autonomous coding tools execute.** There are no downloads or image builds.

## Dell command

After CI merges the implementation, run in Konsole:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/isolated_openai_probe.py --model qwen3.5:4b
```

The command launches an isolated Node script from the local Python controller; it invokes one bounded local inference call and writes **ignored local evidence** at `artifacts/m0/isolated-openai-wire.json`.

Expected success begins with:

```text
M0.8b-1 ISOLATED OPENAI WIRE ISOLATED_WIRE_PASS: qwen3.5:4b
```

Eight negative/positive checks must all report PASS and the container exit status must be 0. If any check fails, leave M0.8b-1 BLOCKED and share only the printed check statuses and bounded reason. Do not use sudo, --build, --network=host, --privileged, broader bind mounts, or weaker permission rules to obtain a pass.

## Remaining hard gates

- **M0.8b-2:** verify the exact request shapes and provider discovery behavior from **OpenCode 2.3.0 itself**; the native /api/show POST and any needed routes must be strictly validated before opening them. Apply a real isolated runtime with no generated tool execution or host credentials and prove model/provider pinning and fail-closed behavior. The current broker deliberately does not offer an unrestricted OpenAI-compatible proxy.
- Validate live streaming rather than only complete bounded SSE replay; streamed messages may require additional OpenCode-specific event fields.
- Verify the installed image supply-chain locks, child processes, workspace boundaries, denial of unauthorized paths/Git remotes, cancellation and recovery, and all required negative security tests **before** allowing generated code to execute.
- Run the frozen three coding tasks three times per model with independent functional and safety acceptance. None of those benchmarks has started.

No check here authorizes M1, OpenClaw agent delegation or autonomous coding.

References: https://opencode.ai/docs/providers , https://opencode.ai/docs/permissions , https://docs.ollama.com/api/openai-compatibility , https://docs.podman.io/en/latest/markdown/podman-run.1.html
