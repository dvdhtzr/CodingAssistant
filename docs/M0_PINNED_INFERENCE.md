# M0.6 — bounded local inference through rootless Podman

**Prerequisites (Dell, owner-reported):** M0.3 OpenCode discovery PASS, M0.4 rootless container isolation PRECHECK_PASS, M0.5 private metadata bridge PRECHECK_PASS with loopback TCP available.

M0.6 is the next incremental integration gate: verify that a local HTTP request inside a **network-disabled, rootless container** can reach an exact pinned Ollama model through a local HTTP → private Unix socket → fixed host Ollama relay, without allowing arbitrary host access.

## Safety properties

The host relay accepts **one predetermined prompt**, `Reply with the single word READY.`, for the exact requested model tag. It checks Ollama's installed model digest both before and after generating the response; this is a *coarse integrity check*, not cryptographic proof of a model snapshot. The relay reconstructs the entire upstream request, forces `stream: false`, `think: false`, `temperature: 0`, and `num_predict: 64`, and rejects additional options, tools, alternate models, or arbitrary prompts. Responses are bounded and sanitized; model reasoning isn't passed through.

The container uses rootless Podman with `--network=none`, dropped capabilities, no-new-privileges, a read-only root filesystem, limited processes/memory, and only two restricted mounts (disposable workspace and a read-only private socket directory). Its local TCP proxy binds **only** `127.0.0.1` inside that container and cannot reach public internet hosts. The host-side broker connects only to `127.0.0.1:11434`.

This is **not** a general-purpose OpenCode-compatible API and **does not execute AI-generated code**.

## Run on Dell

Confirm local Ollama is running and `qwen3.5:4b` is still installed. The previously pulled `docker.io/library/python:3.13-slim` Podman image is reused. Then run a single copy/paste line in Konsole:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/inference_transport_probe.py --model qwen3.5:4b
```

The script performs one **real** Ollama inference request; on the Intel i5 it can take time. It neither downloads a container image nor installs packages, modifies other projects, or runs OpenCode.

Expected result: `M0.6 ISOLATED INFERENCE INFERENCE_PASS: qwen3.5:4b` with eight checks marked PASS, including pinned inference, denied outbound TCP, denied alternate model, denied unsupported path, and access only through the expected local transport.

If the command reports `BLOCKED`, stop and share the exact failed check and reason. Never switch to `--network=host`, `--privileged`, mounting your home directory, or running Podman with sudo to get past the failure.

Private evidence is written under ignored `artifacts/m0/inference-transport.json`; do not commit machine inventory, model output or secrets to public GitHub.

## Remaining gates

- Upgrade the relay to a *strictly bounded*, model-pinned OpenAI-compatible interface with tool-call semantics and streaming suitable for the **actual** supported OpenCode version.
- Build a pinned offline execution image containing OpenCode, .NET 10, Node and necessary runtimes, with no uncontrolled package downloads or outbound network.
- Validate real worktree and process restrictions, symlink/path traversal, child processes, Git remotes, cancellation/recovery, and all ten mandatory security negative tests before executing untrusted generated code.
- Run the three frozen coding exercises three times each with independent acceptance and independent safety review.

M0 remains **YELLOW** until coding benchmark evidence and security requirements are satisfied.

References: citeturn289848search1turn289848search2
