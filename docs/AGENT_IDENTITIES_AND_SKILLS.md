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
Project-specific owner review and revocation are described below. The catalog itself does not supply any text to MAF or Ollama and
cannot grant a tool or execution permission. Existing agent instructions and approvals are unchanged.

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
to projects. The Solution Architect can now use a current review via the separate
per-recommendation opt-in described below. There is no standing activation.

Still outstanding: phase-specific exact task activation and usage
evidence, native MAF skills compatibility, reference loader and
independent live local Ollama acceptance. No external installation,
script execution, network or filesystem access is enabled.

## First role-specific activation increment (#110)

The Solution Architect can optionally use the reviewed
`architecture-tradeoffs` skill for **one** architecture recommendation.
After reviewing the skill in Skills, open Solution Architecture and select
the per-topic checkbox before requesting a recommendation. The checkbox
resets on submission. Unselected recommendations use the original model
path with no skill context.

The existing recommendation POST accepts optional `skill` with
`name`, `reviewRevision`, `reviewSha256`, and a fresh 32-character
lowercase hexadecimal `requestId`. The host checks current project
review, exact skill identity/role/scope, approved spec version and
hash, current decision revision and selected local model before
admitting an immutable single-use context in SQLite. Unknown skill
names, replay, stale review/revision, unapproved specification and
unsupported adapters fail closed. Only the fixed embedded instructions
are appended to the MAF Solution Architect's policy as subordinate
advisory context. No native tools, scripts or filesystem/network
permissions are added. The normal strict proposal validation and
separate owner decision approval still apply.

Authenticated GET `/api/tasks/{id}/skill-uses` exposes bounded usage
records; the Skills destination displays them. `Admitted` is not proof
a call ran (a stopped process may leave its outcome unknown).
`ProposalReturned` means the model response parsed successfully,
not that a proposal was persisted or approved; `CallFailed` does not
claim the model returned a result. Each record binds the exact review,
content hash, spec, decision topic/revision and selected model. An
in-flight request retains its already-admitted context; revocation
blocks future admissions. Project deletion removes these records.
No standing activation is granted.

This uses the existing MAF ChatClientAgent instruction context path
rather than a native portable-skills API. Model-free tests exercise the
actual adapter with a fake IChatClient; a real local Ollama owner test
is still required. Safe reviewed references, native skills API compatibility and full live
acceptance remain outstanding.

## Product Owner activation increment (#110)

After reviewing `requirements-discovery` for a project, the Product
Discovery and Specification stages offer a separate checkbox for one
model-driven interview turn or specification draft. It resets on
submission. Ordinary calls never receive ambient skill context.

Both existing POST contracts accept optional `skill` containing the
name, review revision, review SHA-256 and a fresh request ID. The host
rechecks the current review and exact saved transcript sequence in a
transaction, reconstructs the context from the persisted objective,
transcript and pending owner answer (if any), validates the local
model and phase, and binds the context digest to an admitted event.
No model-supplied skill paths, text, tools or execution rights are
accepted. A single-use context cannot cross interview and specification
phases. Existing bounded retries and output validators remain intact.
The deterministic final interview acknowledgement rejects skill
activation, since it does not call a model.

The authenticated skill-uses endpoint includes separate
`discoveryEvents` records with expected sequence and context SHA-256,
rather than pretending an approved specification already exists.
`ResponseReturned` means the Product Owner adapter returned a
validated result (including the existing bounded repetition fallback),
not that the artifact was saved or approved, or that instructions were
obeyed. `Admitted` without a terminal receipt is an unknown outcome.
Review revocation prevents future admissions; admitted calls retain
their immutable context. Project deletion removes the audit records.
Model-free MAF adapter tests do not qualify live Ollama.

## QA Engineer activation increment (#110)

After reviewing the `static-web-verification` skill, select the
one-job opt-in before requesting independent static QA of saved
source. The optional `skill` field on the existing QA start request
contains the exact name, review revision/hash and new request ID.
Only the approved static-web architecture profile is eligible.
An explicitly selected skill cannot fall back to a non-MAF reviewer.

QA admission and the skill-use record commit in the same SQLite
transaction, after existing current source, specification,
architecture and approved plan checks. The skill context is bound
to the new review ID, exact source run and file fingerprint, plan,
architecture and selected model. Each file consumes that context
once; existing bounded QA corrections and read-only Code Atlas
observations remain unchanged. The reviewed instructions provide
advisory context only and cannot add callable tools, filesystem
access, execution, package installation or test claims.

The usage endpoint adds `qaEvents`, joining each admitted skill
selection to its actual QA review state and count of saved file
reports. Running, failed and interrupted states are not completed
reviews. Static reports are not evidence of a successful build,
runtime verification or repaired behavior. Revocation blocks
future job admissions, not an already admitted immutable job.
Project deletion removes skill usage records before their parent
QA review. Explicit selection applies only to that requested job;
automated repair-cycle QA never inherits it.

Model-free tests exercise the real coordinator and MAF adapter with
an injected deterministic IChatClient, including rejected review,
scope mismatch, adapter fallback, replay and revocation. This does
not qualify real Ollama or generated-code execution. Native MAF
skills API and safe reference support still require separate review
and live acceptance.
