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
Passing CI validates the M2 API contract, not Dell-host packaging, M1
production security, WebSockets/SignalR, full run orchestration or M3 UI.

## Follow-up product slices and backlog

1. **M2 follow-up:** authenticated SignalR progress and observer reconnect
   backed by durable run records, then an explicitly scoped adapter to the
   already-proven M1 real coding runner with exact-plan matching and no silent
   retry. Complete cancellation and integrated negative security verification.
2. **M3:** React Mission Control for task/plan review, approval, status and
   diffs, with owner-friendly run controls.
3. **Tracked debt:** GitHub issues #68–#72; #68 must be closed before the
   optional M1.14 Podman recovery probe is used. Debt does not authorize
   weakening any safety-critical execution gate.
