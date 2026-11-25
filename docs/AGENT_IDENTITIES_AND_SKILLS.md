# Phase 4–5 — Independent MAF agents and future portable skills

**Status:** The five named MAF agents are distinct, host-owned identities. A
fully pluggable skill loader is **not yet implemented**. A read-only fixed
candidate catalog is available; see the discovery increment below.

## Independent identities (first-class, immutable)

`EngineeringAgentRoster` defines five separate agents with
role, goal, fictional professional backstory, working style, future
allowlisted skill scopes and execution authorization:

| Role | Mission | Current authority |
| --- | --- | --- |
| Product Owner | Discover requirements, preserve acceptance | Interview; propose spec; never approve |
| Solution Architect | Compare architectures with explicit tradeoffs | Propose choices; never approve |
| Technical Lead | Break owner-approved work into dependent tasks | Propose revisioned engineering plan; never approve |
| Software Engineer | Implement ONE assigned, approved task at a time | Source-only file changes; no executing or testing |
| QA Engineer | Independently inspect submitted source and possible defects | Bounded static review only; no build or test PASS |

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
no privilege. There is currently **no** external skill import, network download,
script execution, filesystem access or arbitrary tool/command handler
in any of these five agents. That is intentional until security
qualification is completed.

**M8.16 additive capability:** The Software Engineer may request one of three
narrowly host-brokered, read-only Code Atlas inspections of already saved
source (symbol search, explicit relationships, source excerpt). A validated
JSON request is handled by the host, and evidence is returned to the agent
in the next MAF turn. Only two requests per approved task are allowed.
The local Ollama adapter still denies native function tools, and no engineer
or QA agent gets shell, build, network, installation or arbitrary filesystem
access. Host-issued observations are recorded in the owner-private run ledger.
See CODE_ATLAS.md section M8.16. This is an observe/act loop, not a qualified
autonomous coding executor.

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
Once it is fully generated, the independent QA Engineer can be explicitly
asked to inspect each saved source file. QA produces potential findings,
source evidence and manual test suggestions, NOT a passed runtime test.
The legacy source generator remains fully available. Source is NOT
automatically executed or independently verified; actual sandboxed build,
browser checks and repair loops remain future qualified milestones.
See [INDEPENDENT_QA_PHASE5.md](INDEPENDENT_QA_PHASE5.md).

## Discovery increment (#110)

Authenticated `GET /api/agents/skills` exposes three repository-owned
embedded `SKILL.md` candidates (requirements discovery, architecture
tradeoffs, static web verification), with role/scope, pinned source
commit and canonical UTF-8 LF SHA-256, version, content limit and
empty required tool categories. The Skills destination can inspect
the actual instructions and pins. All entries are
`DISCOVERED_NOT_APPROVED`, inactive, and carry an unspecified
repository-owned license marker (no third-party license is assumed).
Approval requires an explicit license review.

Only the three fixed manifest resources are read; no filesystem path
or package ID from a request is opened. The deliberately narrow
frontmatter subset is two plain scalar fields, with bounded content
and exact pinned hashes. General YAML, references, third-party
installation, activation, and activation audit are not implemented yet.
Project-specific owner review and revocation are described below. The catalog does not
supply any text to MAF or Ollama and cannot grant a tool or execution
permission. Existing agent instructions and approvals are unchanged.

The existing API project references Microsoft.Agents.AI 1.28.0 on
.NET 10 and uses the local Ollama chat client. Native skills API
compatibility and live local skill activation still require separate
validation. #110 remains open until its complete acceptance criteria
are satisfied.

## Owner review increment (#110)

The Skills destination now offers project-specific review of the three
fixed embedded candidates. Authenticated GET
`/api/tasks/{id}/skill-reviews` returns review state and history. POST
`/api/tasks/{id}/skill-reviews/{name}` records an explicit review or
revocation. The SHA-256 review fingerprint binds schema, project,
source commit and URL, version, content digest, license declaration,
compatibility, role, scope, byte limit, and required tool categories.
The expected revision prevents stale/replayed writes. SQLite records
ordered, bounded events; project deletion removes its review records.

An owner must confirm permission for **local instruction use** and
enter the exact displayed `APPROVE LOCAL USE <reviewSha256>` phrase.
The repository license marker is unspecified; this attestation does
not create or infer a redistribution license. Revocation similarly
requires `REVOKE <reviewSha256>`. A changed descriptor or hash makes
the prior review stale; it does not silently transfer to another role,
project, version, or package. A current approval is
`APPROVED_LOCAL_REVIEW_NOT_ACTIVE`, never an execution grant. The
global catalog remains unapproved/inactive because reviews are local
to projects. No agent reads this ledger yet.

Still outstanding: phase-specific exact task activation and usage
evidence, native MAF skills compatibility, reference loader and
independent live local Ollama acceptance. No external installation,
script execution, network or filesystem access is enabled.
