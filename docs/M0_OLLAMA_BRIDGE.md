# M0.5 — Local Ollama access without container network access

The Dell passed M0.3 (model discovery) and M0.4 (seven rootless isolation checks) as observed in the owner-provided console output. **The next missing capability** is allowing a network-disabled, rootless coding container to use Ollama **without** opening arbitrary host or internet access.

## Design: explicitly narrow IPC broker

A transient owner-controlled host process listens on a private `0700` directory and a `0600` AF_UNIX socket. The rootless container mounts **only** its disposable workspace and the broker directory (read-only mount), while retaining `--network=none`, `--cap-drop=all`, `--read-only` and `no-new-privileges`.

The broker presently supports **only** `GET /api/version` and `GET /api/tags` against fixed `127.0.0.1:11434`. It deliberately denies writes, chat, arbitrary paths, redirects and remote host forwarding. The goal is to prove IPC reachability and boundaries, not to execute inference yet.

M0.5 also probes whether an HTTP-compatible loopback listener works inside a `--network=none` container. OpenCode uses a regular HTTP provider endpoint, so this is a genuine feasibility decision point. When loopback is unavailable, the output is `BRIDGE_ONLY`, not `PRECHECK_PASS`; no network restrictions are weakened automatically.

## Run on Dell

Requires the same rootless Podman and already-downloaded `python:3.13-slim` image used in M0.4. With Ollama running:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/bridge_probe.py
```

Expected outcomes:
- `M0.5 LOCAL OLLAMA BRIDGE PRECHECK_PASS`: seven fixed IPC, deny, and host-isolation checks pass and TCP loopback works.
- `BRIDGE_ONLY`: IPC checks pass, but isolated TCP loopback is unavailable; alternate translation needed before an OpenCode run.
- `BLOCKED`: actual IPC or isolation check failed. **Do not proceed to agent execution**; share the failing check and reason.

The program launches **only fixed, model-free Python** in the disposable container. It does not install software, change host firewall settings, enable internet access, or execute AI-generated code. Evidence is written to ignored `artifacts/m0/bridge-preflight.json`. No files need to be manually created.

## Future proof obligations before any coding agent

1. Extend the allowlisted broker with model-pinned, bounded Ollama chat/completion and streaming operations.
2. Verify isolated loopback TCP-to-Unix translation or another supported OpenCode connection, without container network egress.
3. Create a pinned, offline runtime image containing OpenCode, .NET 10, Node/npm and required SDK dependencies. Verify image digest and licenses.
4. Explicitly restrict all subprocesses, source mounts, package installs, Git remote operations, and OS capabilities; run negative security tests.
5. Run controlled 3 tasks × 3 repetitions with independent build, UI and API acceptance evidence. Protect user data and credentials.

M0.4 and M0.5 success are **preflights**, not full M1 certification or M0 model qualification.
