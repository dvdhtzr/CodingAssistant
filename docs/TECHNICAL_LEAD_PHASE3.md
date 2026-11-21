# Phase 3 increment — MAF Technical Lead and durable implementation planning

**Date:** November 21, 2026  
**Implementation:** Third real MAF planning participant; host-owned durable transition.
**Approval is not execution:** No new model tool privileges or runtime capabilities.

## Owner workflow

1. **Product Owner** conducts the local Ollama interview. Owner approves
   the exact immutable specification version/SHA.
2. **Solution Architect** proposes and the owner selects **both** technology
   stack and architecture style. Server computes the current architecture SHA.
3. **Technical Lead (MAF `ChatClientAgent`)** is invoked with the same
   explicitly selected local Ollama model, the owner-approved specification,
   and *both* owner-selected architecture choices. It proposes a structured
   implementation plan: ordered task IDs, descriptions, dependencies,
   acceptance criteria, independent test strategy and risks.
4. Host independently validates that output; accepted plans are versioned,
   immutable and **Proposed**, never automatically approved.
5. Owner explicitly reviews and approves the exact plan SHA. A new
   proposal supersedes the previous revision and requires fresh approval.
6. An owner-approved plan enters the **engineering backlog**. This is an
   auditable planning handoff, **not** a claim that a Software Engineer agent
   has started, produced files, or tested anything.

The source-only generator is not modified in this milestone. Historical
projects remain usable and do not need a Technical Lead plan to download or
generate source. The next integration milestone will bind individual
approved engineering tasks to a Software Engineer and independently evaluate
the generated results behind separately qualified execution boundaries.

## API and persistence

Authenticated, owner-only HTTP:

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/tasks/{id}/implementation-plan` | Latest version and immutable history |
| POST | `/api/tasks/{id}/implementation-plan/generate` | Invoke real MAF Technical Lead, validate and propose revision |
| POST | `/api/tasks/{id}/implementation-plan/approve` | Exact owner approval of current plan hash |
| GET | `/api/tasks/{id}/agent-handoffs` | Merge saved role transitions and plan/backlog handoffs |

Generate request: `{schemaVersion:1,specificationVersion,specificationSha256,
architectureSha256,expectedRevision}`. The same specification/architecture
binding is checked immediately before agent inference **and again within the
SQLite proposal transaction**, protecting against human edits while an LLM
request is in flight. The expected revision is checked before and after
inference. A model can never self-approve.

Approve request: `{schemaVersion:1,expectedRevision,planSha256,
confirmation:"APPROVE <plan-sha256>"}`. Owner click sends the exact confirmation
for the displayed plan only. The server verifies current approved spec,
both choices, latest revision, complete content digest and owner confirmation
in one transaction. A stale revision or approval cannot regain authority.

The private `implementation_plans` table holds schema version, task,
specification version/hash, architecture SHA, model, validated JSON outline,
canonical SHA, status, created/approved times. Immutable revision history
survives application restarts. Old revisions become **Superseded** and plans
are reported **STALE_APPROVALS** when approved requirements or architecture
change. Project deletion transaction also deletes these records.

The MAF handoff inspector shows **ArchitectureHandoff** (owner-approved
choices made available to Technical Lead), **PlanProposed**, and
**PlanApproved** (human-approved engineering backlog). Every event includes
real saved model, version/hash, date and freshness. No fabricated agent work
is displayed.

## Local MAF constraints

`MafAgentTeam` now has three actual MAF `ChatClientAgent` identities.
`LocalMafChatClient` pins inference to the loopback Ollama API, rejects
tools, never uses an HTTP proxy or follows redirects, and has no cloud
fallback. Technical Lead output uses bounded JSON, is parsed against typed
contracts, permits one constrained correction of invalid output, and
cannot include executable commands or unrestricted file access as a
trusted instruction.

The Product Owner is interactive and owner approvals can span days. Current
orchestration is **host-managed with persistent typed MAF handoffs**, not
yet a native resumable `WorkflowBuilder` graph or `RequestPort` checkpoint.
Native checkpointed workflow orchestration is the next increment to assess,
rather than replacing a working SQLite human-approval boundary prematurely.

## Acceptance

Model-free GitHub CI on both Windows and Linux:
- Real MAF `ChatClientAgent` invocation using a fake `IChatClient` verifies
  the distinct Technical Lead role/instructions and exact selected model.
- HTTP/SQLite rejects missing or mismatched owner approvals and cannot call
  the planner on invalid input; approves only current immutable plan SHA.
- Regeneration creates a new revision and supersedes history; old approvals
  cannot be replayed. Changes to architecture mark plans stale.
- React stage renders within the six-stage, optionally zero-open Project Journey.
- Existing Product Owner/Architect and source generation/ZIP regression checks
  remain green; no independent build/test claims.

**Live acceptance (owner's Windows machine):** Update to `main`, open
an approved project, expand *Implementation Plan*, ask the Technical Lead to
plan the build, inspect dependencies and acceptance criteria, approve the
plan, and verify that the inspector displays the saved handoffs. Reopen after
restart to confirm persistence. This owner-observed Ollama quality test is
distinct from model-free CI; no source ZIP is independently verified.
