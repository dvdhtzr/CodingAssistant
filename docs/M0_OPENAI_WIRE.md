# M0.8a — OpenAI-compatible Ollama wire preflight (no agent)

**Purpose:** Before connecting the real isolated OpenCode 2.3.0 process to Ollama, verify that the exact locally installed model can return a *streamed function call* through Ollama's local OpenAI-compatible `/v1/chat/completions` endpoint. This is a **host-loopback-only** contract test, not an isolation test or an autonomous coding benchmark.

## Run on Dell

From Konsole:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/openai_wire_probe.py --model qwen3.5:4b
```

Prerequisites: the selected exact local Ollama model is installed, Ollama is reachable at `127.0.0.1:11434`, and M0.6/M0.7 have passed.

The script checks the exact model tag and digest using `/api/tags`, the `tools` capability from `/api/show`, and the advertised model from `/v1/models`. It then sends exactly **one fixed prompt** asking the model to return a `report_ready(status="READY")` function call through **streaming** `/v1/chat/completions`. Only that one harmless function is exposed in the request. **No function is executed**, and the streamed output is parsed into a small allowlisted status, never passed to a shell or copied into project code. The digest is checked again after the request (a coarse tag-stability check, not a cryptographic inference attestation).

Success requires `M0.8a OPENAI WIRE WIRE_PASS` with five checks PASS. Malformed framing, wrong model, truncated streams, multiple/unknown tools, unexpected arguments, missing `[DONE]`, version mismatch or absent model capability fail closed. Model failure to select the advertised function is a real **BLOCKED** result and must not be disguised as a transport pass.

Local ignored evidence: `artifacts/m0/openai-wire.json`. Do not publish raw model outputs or private machine inventory. There are no package downloads, configuration changes, OpenCode sessions, repository edits or generated-code executions.

## Limitations and next gate

This checks only the **host-side Ollama wire contract**. It does **not** prove that OpenCode sends the same payload, uses streaming/tool calls through our private Unix socket bridge, or can run safely in an isolated worktree. The current M0.6 broker still intentionally accepts only a single fixed native `/api/chat` request; do **not** open a general-purpose relay merely to achieve an early pass.

**M0.8b:** capture and validate the actual OpenCode 2.3.0 request shapes; implement the smallest required model-pinned OpenAI-compatible interface through rootless Podman/AF_UNIX/isolated loopback; enforce request/response/time/tool limits; prove provider pinning and lack of public network access. A model's *returned* tool call is not authorization to execute it. Autonomous coding and 3×3 benchmarking remain disabled pending the separate workspace security, recovery and negative tests.

Official references:
- https://docs.ollama.com/api/openai-compatibility
- https://docs.ollama.com/capabilities/tool-calling
- https://opencode.ai/docs/providers
- https://opencode.ai/docs/models
