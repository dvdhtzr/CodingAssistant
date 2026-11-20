# M2→M1 executable integration — issue #76 (qualification pending)

This is an **implementation under review**, not a declaration that M1 is GREEN.
The supported initial coding target is **only** the prequalified disposable
`02-build-repair` C# fixture, with exactly `api/Program.cs` writable.
The original source repository, Git history, other fixtures, environment
secrets, and arbitrary host projects are never exposed to the agent.

## Critical two-approval boundary

An M2 task's immutable planning hash and the M1 executable hash are **different**.

The M1 executable plan is recomputed on the Dell from:
- M2 task ID, run ID, exact current planning version and planning hash;
- fixed coding assignment, exact writable file, source/manifest/fixture digests;
- exact locally pinned Qwen model identity **and host-verified digest**;
- immutable Gateway/OpenCode and offline evaluator image IDs;
- network/Git restrictions and pinned Gateway RPC method.

The .NET API independently canonicalizes and SHA-256-hashes the full M1 plan,
then verifies this exact fixed scope. A changed M2 plan, model digest, image,
source fingerprint, evaluator, fixture, file scope or control-plane binding
changes the executable hash and requires a new reviewed approval.

M2 planning `APPROVE <m2-plan-hash>` **never** authorizes a coding run.
The owner must separately supply **`APPROVE <m1-executable-hash>`** to
`POST /api/runs/{runId}/execution-authorization`. The hash is not shortened.
Authorization does not automatically launch an agent: the owner must explicitly
`POST /api/runs/{runId}/start`. One successful admission is durable; an
interrupted or completed request is **never** automatically retried.

## Default-denied host configuration

New execution endpoints are unavailable (HTTP 503) unless the operator sets:

```bash
export CODINGASSISTANT_ENABLE_LOCAL_EXECUTION=1
export CODINGASSISTANT_REPOSITORY_ROOT="$HOME/CodingAssistant"
```

The existing `CODINGASSISTANT_OWNER_TOKEN`, loopback-only binding, and private
`CODINGASSISTANT_DATA_DIR` rules remain mandatory. Never put the owner token
in a URL, JavaScript localStorage, GitHub, a screenshot, or a chat transcript.

Real execution is **Linux-only** at this stage. Windows model-free integration
CI is required, but real Windows/WSL2 runtime and security acceptance have not
been performed. A non-Linux deployment uses the disabled runner by default.

The configured Python child is launched directly with argument arrays, never a
shell. Only selected local-session environment keys are inherited. In
particular the API bearer token and any cloud-provider credentials are not
passed to the runner, and executable approval travels through a private stdin
pipe, **not** an argument, environment variable, logfile, or URL.

## Preparing the immutable M1 derivative — one-time local action

The source fingerprint necessarily changes after this adapter is merged. On
the Dell the original M1.12 derived image must therefore be rebuilt **offline**
against the already-pinned owner-composed image:

```bash
cd ~/CodingAssistant && git switch main && git pull --ff-only && python3 tools/m1/approved_gateway_coding.py --prepare-only
```

This option verifies rootless Podman, immutable image and model provenance,
performs only the reviewed offline copy-only derived-image build, then prints
the fixed executable plan. It **does not** create an M1 run, start an ACP
session, prompt a model, or grant approval. A missing local image, evaluator,
manifest, digest or rootless boundary fails closed.

The host .NET application requires a .NET 10 runtime/SDK **or** the
self-contained `codingassistant-linux-x64` artifact built by the Ubuntu PR
CI job after model-free tests pass. That artifact can be retrieved from the
PR's Actions run and unpacked under a private directory on the Dell:

```bash
mkdir -p "$HOME/.local/opt/codingassistant-api"
unzip -oq "$HOME/Downloads/codingassistant-linux-x64.zip" -d "$HOME/.local/opt/codingassistant-api"
chmod u+x "$HOME/.local/opt/codingassistant-api/CodingAssistant.Api"
export CODINGASSISTANT_OWNER_TOKEN="$(python3 -c 'import secrets; print(secrets.token_hex(32))')"
export CODINGASSISTANT_ENABLE_LOCAL_EXECUTION=1
export CODINGASSISTANT_REPOSITORY_ROOT="$HOME/CodingAssistant"
"$HOME/.local/opt/codingassistant-api/CodingAssistant.Api"
```

The actual downloaded artifact ZIP filename may be browser-adjusted; use its
real name. Keep the owner token only in your private session, and use the same
token in another terminal for authenticated HTTP calls. No model starts merely
because the API is running. Do not grant Podman socket mounts or elevate the
API to root to work around a missing host SDK. The release artifact is a
CI-built binary, **not** a Dell runtime/security qualification by itself.

## Owner-facing authenticated workflow

Using the authenticated M2 API:

1. Create a task; draft and approve its **M2 planning** hash.
2. Create one run intent against the exact current approved M2 plan.
3. `POST /api/runs/{id}/execution-plan` with an empty JSON object. The
   non-executing Python preflight returns M1's fully pinned plan.
4. Review the returned `plan` and full `planHash`. The plan is durably
   recorded in SQLite; a repeated identical preview is idempotent.
5. `POST /api/runs/{id}/execution-authorization` with
   `{"approval":"APPROVE <exact-m1-executable-hash>"}`.
6. `POST /api/runs/{id}/start` with an empty JSON object. This is the
   explicit owner operation that admits one fixed-fixture execution.
7. Read `GET /api/runs/{id}` and replay
   `GET /api/runs/{id}/events?after=<cursor>` after subscribing to
   authenticated SignalR `/api/progress`.
8. `POST /api/runs/{id}/execution-cancel` to request termination of this
   run only. It does not terminate other user processes or Podman guests.
9. If an interruption/uncertain cleanup reports `RecoveryBlocked`, no
   automatic retry occurs. `POST /api/runs/{id}/execution-recover` performs
   only model-free, label-verified M1 reconciliation. `Interrupted` is not
   success; a new task/run/approval must be created for another attempt.

Event kinds and states are bounded, fixed-category, monotonically numbered,
and committed in SQLite. SignalR notifications are hints, not persistence.

## Safety, limitations and acceptance

- M1 rootless Podman process, file and network containment remains mandatory.
- The broker only forwards requests for the pinned local model/digest. No
  paid inference, provider switching, Git remotes, or cloud fallback.
- Runtime success is accepted only from the M1 runner after its independent
  offline .NET evaluator verifies the disposable repaired project.
- An unexpected runner exit, cancelled subprocess, incomplete cleanup, or
  uncertain journal result becomes `RecoveryBlocked`, **not** success.
- Startup marks previously active states `RecoveryBlocked`; it never
  replays model work. Explicit, scoped cleanup confirmation is required
  before treating an interrupted run as safely closed.
- The existing M1.14 optional recovery probe remains blocked by issue #68.
- Ten **integrated** adversarial/security-negative cases, including actual
  symlink/path traversal, child-process, remote-network and Git escapes,
  and real Dell positive/negative execution still require acceptance.
- A model-free Ubuntu/Windows GREEN is **necessary but not sufficient**.
  No direct Windows runtime qualification can be inferred from Dell results.

This implementation does not introduce an arbitrary repository execution
capability, UI, or third-party skill loader. M3 Mission Control follows
successful qualification of this exact bounded integration.
