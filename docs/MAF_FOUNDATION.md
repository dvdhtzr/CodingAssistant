# Phase 3 — Microsoft Agent Framework foundation

**Date:** November 21, 2026  
**Scope:** Real local MAF planning agents; no code execution.  
**Package:** `Microsoft.Agents.AI` pinned at **1.28.0**, stable on NuGet at implementation time.

## Running architecture

The original `.NET 10` HTTP/SQLite project remains the source of truth for
identity, the owner conversation, specifications, decisions, approvals and
artifact authority. Normal production requests now invoke **actual**
`Microsoft.Agents.AI.ChatClientAgent` participants:

1. **ProductOwner** — `MafAgentTeam.AskAsync` and `DraftAsync` with
   `LocalInterviewModel`'s existing structured prompts, validators, and
   bounded one-retry behavior. It consumes the saved owner/assistant turns,
   never a fabricated or temporary conversation.
2. **Owner approval** — existing authenticated `InterviewStore.Approve`,
   exact version/SHA. The application, **not** any agent, decides approval.
   Only then is a versioned `SpecificationHandoff` event added.
3. **SolutionArchitect** — `MafAgentTeam.RecommendAsync` using the exact
   approved specification, model and requested topic. Returned alternatives
   are validated with the existing `LocalArchitectureAdvisor.Parse` and
   `ArchitectureDecisionStore.Propose`; model recommendation never becomes
   owner approval automatically. A saved proposal records a handoff to the
   owner for independent selection.
4. **Source Generation** — existing architecture-bound generator, unchanged.
   This still produces **unverified draft text**, not compiled or tested code.

This is real MAF agent execution with a **host-managed, human-gated handoff
sequence**. It is **not** a claim that MAF `BuildSequential` or `BuildHandoff`
now runs across a persistent approval pause. Microsoft notes that simple
sequential orchestration does not support the Product Owner's interactive
feedback loop between agents; a dedicated RequestPort/handoff workflow is a
future increment. The first milestone deliberately retains the reviewed host
workflow until durable resume/approval integration can be implemented safely.

## Local-only model adapter

`LocalMafChatClient` implements `Microsoft.Extensions.AI.IChatClient` and is
used by the actual MAF `ChatClientAgent` instances. It accepts ONLY the exact
selected Ollama model name and sends bounded non-streaming `api/chat`
requests to `http://127.0.0.1:11434`. It never honors ambient HTTP proxies,
redirects, provider endpoints, remote deployment aliases or tool/function
calls. MAF system instructions must be preserved as developer-provided
system policy. Responses remain untrusted and pass existing shape, size and
specification or architecture validation. The existing Ollama model catalog
check still precedes requests.

The MAF agents have **no** tools, shell, filesystem, code execution,
container, skills scripts, repository access or cloud provider. The exact
owner-selected model is reused by both participants; no automatic provider
or model fallback is configured.

## Provenance and UX

The `maf_handoffs` SQLite table records immutable task, specification
version/hash, event kind, from/to agent identity, selected model, proposal
revision and timestamp. The read-only authenticated
`GET /api/tasks/{id}/agent-handoffs` endpoint returns historical events
and whether each event still matches the current approved specification.
Deleting a project removes this record in the existing atomic deletion.

Mission Control's inspector renders saved handoffs and offers refresh.
It does **not** fabricate live progress, agent tokens, or execution readiness.
The sidebar currently has two MAF-backed planning roles; the other four
engineering roles remain explicitly planned.

## Acceptance and remaining work

Cross-platform CI must compile and run the existing API/Phase 2 smoke suites,
the real MAF `ChatClientAgent` tests against an injected non-network
`IChatClient`, strict ownership/approval tests, and React rendering tests.
The injected-chat tests prove actual MAF invocation and data boundaries,
not local Ollama model quality.

**Live-owner test:** On Windows with Ollama running, create a new project;
answer at least two Product Owner questions; review and approve a specification;
request both Architect proposals, inspect the MAF handoff timeline; verify
saved state persists after a restart and no credentials or cloud connections
appear. Record model, host and transcript evidence separately. Existing
projects should remain readable. No automatic migration or replacement of
their saved approval records is permitted.

**Next Phase 3/4 work:** Add vetted instruction-only skill descriptors and
MAF skill loading, strengthen per-agent durable turn provenance, add Technical
Lead/Engineer/QA/Security agents under typed contracts, and only later
implement sandboxed independent test/repair after issue #76 and #92
security/admission requirements are verified. Do not infer that MAF can
independently execute or verify a generated ZIP.
