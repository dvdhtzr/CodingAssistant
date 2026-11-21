# Phase 4 — Independent MAF agents and future portable skills

**Status:** The four named MAF agents are distinct, host-owned identities. A
fully pluggable skill loader is **not yet implemented**.

## Independent identities (first-class, immutable)

`EngineeringAgentRoster` defines four separate agents with
role, goal, fictional professional backstory, working style, future
allowlisted skill scopes and execution authorization:

| Role | Mission | Current authority |
| --- | --- | --- |
| Product Owner | Discover requirements, preserve acceptance | Interview; propose spec; never approve |
| Solution Architect | Compare architectures with explicit tradeoffs | Propose choices; never approve |
| Technical Lead | Break owner-approved work into dependent tasks | Propose revisioned engineering plan; never approve |
| Software Engineer | Implement ONE assigned, approved task at a time | Source-only file changes; no executing or testing |

Every agent creates its own MAF `ChatClientAgent` with a host-owned
instruction policy and exact selected local Ollama model. The host controls
the time, approved inputs and allowed transitions. The agents do NOT have
shared writable memory, can impersonate one another, or approve their own work.

The biography is a *fictional professional work style*, not a human identity,
credential, or proof of expertise. Roles/goals are visible in the Inspector
via authenticated `GET /api/agents/roster`.

## Durable engineering workflow

The owner must explicitly approve the latest Technical Lead implementation
plan with its exact SHA. The new source-only engineering endpoint takes
`{planRevision,planSha256}`. Admission rechecks the current approved
specification version/SHA, BOTH approved architecture decisions and their
fingerprint, and the latest approved plan SHA. The host then gives the
Software Engineer **one dependency-ordered task at a time** with earlier
saved files. Each real MAF invocation proposes 1–3 COMPLETE source file
replacements. A bounded schema, file path, total size and architecture gate
independently validates them. The host records the task completion and
file replacements atomically in SQLite.

Runs have an owner-private ledger of assignments, phases, file revisions,
actual events, error categories and cancellation. Completed tasks survive
stops or model failures, but interrupted runs do NOT auto-resume or claim
completion. A generated engineering ZIP is **unverified source** only.
The old source generator stays available as an independent fallback.

This is a **host-managed, durable MAF agent handoff**, not a native
checkpointed `AgentWorkflowBuilder` graph. The next increment can add
native resumable workflow orchestration once its approval,
cancellation and SQLite consistency requirements are satisfied.

## Proposed portable skill support

Adopt the open **Agent Skills** `SKILL.md` directory convention used by
Anthropic and other runtimes. The host should support:
- A skill directory with a `SKILL.md` containing YAML frontmatter
  (`name`, `description`) and human-readable instructions.
- Optional reviewed reference files, examples, templates and *optional*
  scripts/resources, but **not** automatic execution of those scripts.
- Progressive disclosure: the model sees a short allowlisted catalog,
  and only selected/approved skill instructions are loaded for a task.
- Role scopes (Product Owner skills cannot silently become engineering
  execution permissions; Software Engineer skills cannot approve architecture).
- Version-pinned/audited installations, checksums, explicit opt-in, safe
  size and path limits, and prompt-injection defenses for third-party text.
- A local-first provider-independent loader so the same skills can work
  with Ollama; no mandatory Anthropic API subscription.

The current `allowedSkillScopes` field is **metadata only** and grants
no privilege. There is currently **no** skill import, network download,
script execution, filesystem access or arbitrary tool/command handler
in any of these four agents. That is intentional until security
qualification is completed.

References:
- https://agentskills.io/specification
- https://www.anthropic.com/research/equipping-agents-for-the-real-world-with-agent-skills
- https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/sequential

## Owner acceptance

On Windows, update `main` and restart CodingAssistant. Open a project
with the latest approved Technical Lead plan; in Source Generation choose
**Ask Software Engineer to implement approved tasks**. Observe durable
per-task statuses, file replacements and the Inspector's saved handoffs.
Inspect/download the separate engineering ZIP in Artifacts & Delivery.
The legacy source generator remains fully available. The produced files
are NOT automatically executed or independently validated; verify them
later under the upcoming QA/security milestone.
