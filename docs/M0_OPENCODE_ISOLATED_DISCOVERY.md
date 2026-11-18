# M0.8b-2a — Real OpenCode model discovery inside offline isolation

**Status: PENDING Dell evidence. Overall M0: YELLOW.**

M0.8b-1 passed on Dell: all eight checks, exit code zero. The tested client was a **fixed Node probe**, not OpenCode. This checkpoint runs the *actual pinned OpenCode executable* for **model discovery only**, with no prompt, no coding session, and no inference.

## What happens

The host controller creates a disposable private AF_UNIX socket broker for the exact locally installed and digest-checked `qwen3.5:4b`. It starts the existing immutable-ID M0.7 Podman toolchain image with `--network=none`, `--read-only`, `--cap-drop=all`, no new privileges, non-root user, process/memory caps and an empty private `/tmp` tmpfs using `notmpcopyup`.

Only the private socket directory is mounted, read-only, under `/run/codingassistant`. No user home, project, source code, host credentials, Git repository, or worktree is mounted. A reviewed Node controller starts one loopback server at container-local `127.0.0.1`, advertising only pinned GET `/v1/models` and GET `/api/tags` metadata through the private socket. The **native `/api/tags` response is reconstructed for just the selected model and exact expected digest**; the full host Ollama catalog is never forwarded. All other routes/methods are refused. A local `/api/delete` negative check proves HTTP 403.

Within the container, the controller runs exactly `opencode models`, with a temporary, isolated `HOME=/tmp`, minimal environment, an inline `OPENCODE_CONFIG_CONTENT` that selects `ollama/qwen3.5:4b` and denies other providers, and no tool-execution permissions. It collects only bounded, parsed model identifiers and requested route names—not the raw model catalog, credentials, or response payloads. CLI process time and output sizes are capped.

The gate requires a successful CLI exit, exact local model visible, no unrelated provider/model choices, **observed** request to the private metadata bridge, no unexpected metadata route, blocked external TCP, non-root identity, zero effective capabilities, no-new-privileges and read-only root filesystem. A model showing up only from a local cache **cannot** count as proof of private-bridge communication.

**This is a discovery probe, not the final OpenCode/Ollama integration.** A passing result does not prove inference request shapes, true incremental streaming, tool calls initiated from OpenCode, or autonomous-coding safety. The restricted broker currently does not permit `POST /api/show` or arbitrary `/v1/chat/completions`; if OpenCode requires additional metadata paths, the probe will report the attempted route and remain BLOCKED until another narrowly justified change is reviewed.

## Run on Dell

After the GitHub PR has passed CI and merged, run:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/isolated_opencode_models_probe.py --model qwen3.5:4b
```

Expected only if every check passes:

```text
M0.8b-2a ISOLATED OPENCODE DISCOVERY DISCOVERY_PASS: qwen3.5:4b
```

If BLOCKED, paste the console statuses, container exit code and listed **requested metadata routes**. Keep the ignored local evidence `artifacts/m0/opencode-isolated-models.json` on your machine. Do not run `opencode run`, increase mounts, expose host networking, use sudo, or install cloud-provider credentials to resolve a failure.

## Next gates

M0.8b-2b must validate the actual OpenCode inference path against a restricted, pinned, streaming transport without permitting model-generated tool execution. Then OS-isolated read-only run-mode/child-process security checks, worktree and negative/recovery tests, pinned supply chain and independent 3×3 model coding qualification are necessary. M0 does not turn GREEN based on discovery alone.

Official docs: https://opencode.ai/docs/cli/commands ; https://opencode.ai/docs/providers ; https://opencode.ai/docs/policies ; https://docs.podman.io/en/latest/markdown/podman-run.1.html

## Dell compatibility finding (November 18, 2026)

The first on-device M0.8b-2a run returned BLOCKED even though `opencode models` exited normally and the filesystem, network, privilege and host-file negative checks passed. The sole observed OpenCode metadata request was **GET `/api/tags`**, which the original loopback controller had denied because it exposed only `/v1/models`. `exact_model_visible`, `metadata_reached`, `only_local_provider`, and `no_unexpected_cli_route` correctly failed closed.

The corrective implementation allows only exact GET `/api/tags` (in addition to the existing GET `/v1/models`). The host relay looks up the locally installed Ollama model through its fixed loopback provider, requires one exact name/model and expected digest, and emits a **single filtered model** with bounded standard Ollama metadata fields. It never exposes unrelated host models. Tests verify digest mismatch, duplicate names, malformed metadata, query-string bypass attempts, and unsupported methods/paths fail closed. No inference route is added and no agent run begins.

**The first BLOCKED result remains part of the audit trail.** This is a code compatibility correction, not a gate pass. After the corrective PR has passed CI and merged, re-run the same Dell command above. If OpenCode next asks for `/api/show`, retain BLOCKED and use the observed method/path to design an equally narrow, separately tested response; do not silently permit a generic Ollama proxy.
