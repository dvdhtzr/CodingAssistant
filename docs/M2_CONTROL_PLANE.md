# M2 — local control-plane vertical slice (tasks → plans → exact approval)

This is the **first application-facing slice** of the approved .NET 10 / SQLite
architecture. It builds a real ASP.NET Core Minimal API, not another diagnostic
probe. The existing independently verified M1.12 Gateway → ACPX → OpenCode →
local Qwen runner remains unchanged.

## Current capabilities

- Bind **only** to `127.0.0.1` (default TCP 5098); no public listener,
  wildcard bind, cloud/hosted model, Git remote or unrestricted worktree access.
- All `/api` requests need a fixed-length random **64 lowercase hex**
  `Authorization: Bearer <token>`. Missing/malformed tokens are rejected.
  The token is never written to SQLite or logs.
- Store private tasks and their operator-supplied objective in SQLite.
  Task creation requires an exact 32-lowercase-hex idempotency request ID;
  a replay returns the original record, and a conflicting replay returns 409.
- Draft plans with an explicit `expectedVersion`. Each immutable version
  has a SHA-256 of a canonical schema-tagged payload containing its task ID,
  version, summary, fixed local model and non-executable scope.
  A stale revision returns 409; a revision supersedes prior approval.
- Approve only the **current** plan version with the entire literal
  `APPROVE <64-hex-plan-hash>` text. An approval is stored and idempotent;
  it cannot silently approve a newer revision.
- Read the task list/detail and fixed `/api/status` state.
  Every response explicitly reports `executionEnabled: false` where
  applicable. The API has **no coding/execute endpoint**.

**Important boundary:** an approved database plan is a *control-plane record*,
not an M1 execution approval. It does **not** change the separate M1
owner-authorization checks, permitted worktree, local model digest, rootless
Podman sandbox or evaluator. The M2→M1 execution adapter is a distinct,
later audited slice and must verify its own exact execution plan.

## Next product slice — durable run intents and SignalR progress (issue #75)

The API accepts an authenticated **run intent** only against the currently
approved task/plan/version/hash. This is deliberately *not* a real agent run:
its initial state is `AwaitingExecutionAuthorization`, not `Queued` or
`Running`, and every result has `executionEnabled: false`. Nothing can
call Ollama, OpenCode or Podman through these endpoints.

The newly added persisted run table stores the exact task ID, immutable plan
version/hash, idempotency key, state and highest event sequence. Every create
or cancellation transaction also commits a fixed-category, numbered SQLite
event. Event bodies contain **no objective, source, model output, path, token,
arbitrary message or error text**. Repeated creation returns the original run;
conflicting reuse returns 409. Repeated cancellation cannot append another
event. An old run also exposes `planCurrent: false` when its parent plan has
been revised.

Additional authenticated endpoints:

| Endpoint | Behavior |
| --- | --- |
| `POST /api/tasks/{id}/runs` | `{planVersion,planHash,requestId}` records non-executable intent for an exactly approved current plan |
| `GET /api/tasks/{id}/runs` | History of intents for a task |
| `GET /api/runs/{id}` | Durable current intent state and event head |
| `GET /api/runs/{id}/events?after=0&limit=50` | Numbered immutable events strictly after cursor, max 100/page, returns `nextCursor` |
| `POST /api/runs/{id}/cancel` | Cancel a **pending intent only** and record `IntentCancelled` |
| `/api/progress` | Authenticated SignalR WebSocket endpoint; invoke `WatchRun(runId)` and `UnwatchRun(runId)`, receive `RunUpdated` |

To watch without losing events, **connect/authenticate**, call
`WatchRun(runId)`, then fetch `/api/runs/{id}/events?after=<last_saved_cursor>`
until caught up. Future `RunUpdated` notifications are optional hints;
deduplicate by `(runId,sequence)`. After network failure or process restart,
reconnect and replay using SQLite, **never assume SignalR is durable**.
Only the fixed owner bearer **header** authenticates the hub; query-string
`access_token` is intentionally not supported. Browser-compatible
credential handling will be introduced with Mission Control; do not put
tokens in URLs, query strings, browser localStorage or logs.

The cross-platform smoke test now opens a real authenticated SignalR
WebSocket, watches a run, verifies a live cancellation event matches SQLite,
tests non-owner hub rejection and query-token denial, exercises idempotency
and stale approval rejection, and verifies event cursor replay after restart.
No local model or autonomous code is run in CI.

**Remaining product work:** issue #75 still requires live worker-state and
SignalR progress from the exact approved M1 adapter (#76), plus bounded
cancellation and runtime reconciliation. Issue #77 will add the browser/UI
workflow. These are product gates, *not deferred debt*.

## Fixed-fixture executable integration under qualification — issue #76

The control plane now has a **separate, default-disabled, Linux-only** exact
M1 executable-plan approval path. It does not allow arbitrary source
repositories, models or commands. `APPROVE <M2 planning hash>` remains
non-executable. A distinct, source/image/model/evaluator/worktree-bound M1
execution-plan hash must be previewed and approved before explicit start.

On a qualified Linux owner host, opt-in requires
`CODINGASSISTANT_ENABLE_LOCAL_EXECUTION=1` and
`CODINGASSISTANT_REPOSITORY_ROOT` set to the reviewed checkout.
Additional authenticated routes:

| Endpoint | Contract |
| --- | --- |
| `POST /api/runs/{id}/execution-plan` | Model-free preflight, exact M1 plan preview and persistence |
| `GET /api/runs/{id}/execution-plan` | Read saved executable scope and approval state |
| `POST /api/runs/{id}/execution-authorization` | Exact `{"approval":"APPROVE <full-M1-hash>"}`; no execution |
| `POST /api/runs/{id}/start` | One owner-initiated, journaled fixed-fixture M1 execution |
| `POST /api/runs/{id}/execution-cancel` | Stop this worker only and reconcile trusted resources |
| `POST /api/runs/{id}/execution-recover` | Explicit model-free cleanup after interrupted/uncertain state |

An approved run is never auto-started or auto-replayed after a crash.
Existing `GET /api/runs/{id}`, replayable event cursors and SignalR
`RunUpdated` now include fixed-category execution lifecycle transitions.
A failed or ambiguous cleanup blocks future execution admission rather than
inventing completion.

**Overall M1 remains YELLOW.** Do not use this as an authorization to execute
in an unqualified environment. The local Dell positive/negative gates and
ten required integrated adversarial cases still must pass. See
[M2→M1 execution adapter guide](M2_M1_EXECUTION_ADAPTER.md).

## Running locally (when a .NET 10 SDK with SQLite package restore is available)

On the Dell or a Linux development host, in the repository:

```bash
export CODINGASSISTANT_OWNER_TOKEN="$(python3 -c 'import secrets; print(secrets.token_hex(32))')"
dotnet run --project src/CodingAssistant.Api/CodingAssistant.Api.csproj
```

This command binds only to `http://127.0.0.1:5098`. Keep the token private.
The default private database directory is the user's local application data
folder under `CodingAssistant`. To override it, set
`CODINGASSISTANT_DATA_DIR` to a **private non-symlink directory**. If that
directory is not private or the token is missing, startup fails closed.
Set `CODINGASSISTANT_PORT` to another unprivileged loopback port if needed.

In another terminal with the **same** token in its environment, example:

```bash
curl -sS -H "Authorization: Bearer $CODINGASSISTANT_OWNER_TOKEN" \
  http://127.0.0.1:5098/api/status

REQUEST_ID="$(python3 -c 'import secrets; print(secrets.token_hex(16))')"
curl -sS -X POST http://127.0.0.1:5098/api/tasks \
  -H "Authorization: Bearer $CODINGASSISTANT_OWNER_TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"title\":\"Fix compiler error\",\"objective\":\"Repair my disposable C# sample and preserve behavior.\",\"requestId\":\"$REQUEST_ID\"}"
```

The first response contains the newly created opaque task ID. To draft a plan
and approve it, use the following endpoints with authenticated JSON requests:

| Endpoint | Purpose |
| --- | --- |
| `GET /healthz` | Coarse public health with execution disabled |
| `GET /api/status` | Authenticated capabilities (execution disabled) |
| `GET /api/tasks` | Owner's latest 50 task records |
| `POST /api/tasks` | `{title, objective, requestId}`; requestId must be 32 lowercase hex |
| `GET /api/tasks/{id}` | Task and latest plan |
| `POST /api/tasks/{id}/plans` | `{summary, expectedVersion}`; increments and hashes immutable plan |
| `POST /api/tasks/{id}/approvals` | `{version, approval}`; approval must exactly match `APPROVE <planHash>` |

No frontend is available yet. This REST surface is the foundation for
M3's Mission Control UI, not a substitute for it.

## Verification

```bash
dotnet run --configuration Release --project tests/CodingAssistant.Api.Smoke/CodingAssistant.Api.Smoke.csproj
```

The cross-platform smoke harness builds and starts the **real API**, connects
to an ephemeral IPv4 loopback port, and checks actual HTTP authentication,
SQLite task persistence across restart, request-id idempotency, rejection of
wrong/stale approvals, concurrent plan revisions, non-executable approvals,
and absence of execution endpoints. No LLM, Gateway or coding process runs.

Ubuntu/Windows CI runs this alongside all existing model-free M0/M1 tests.
Passing CI validates the M2 API, local SignalR handshake and event replay
contract, not Dell-host packaging, **real M1 worker coordination**, full run
orchestration or M3 browser credential/UI support.

## Follow-up product slices and backlog

1. **M2 follow-up:** bind the existing durable run record and SignalR observer
   to an explicitly scoped **real** M1 execution adapter (#76), enforcing exact
   coding-plan authorization, no silent retry, bounded interruption/recovery and
   integrated negative security verification.
2. **M3:** React Mission Control for task/plan review, approval, status and
   diffs, with owner-friendly run controls.
3. **Tracked debt:** GitHub issues #68–#72; #68 must be closed before the
   optional M1.14 Podman recovery probe is used. Debt does not authorize
   weakening any safety-critical execution gate.
