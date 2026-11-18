# CodingAssistant contributor and agent rules

Read README.md, docs/IMPLEMENTATION_PLAN.md and docs/SECURITY.md before changing behavior.

- Follow the approved sequential M0 → M1 → M2 → M3 plan. Never mark a gate GREEN without reproducible evidence.
- Implementation requires owner authorization; future product-managed coding requires approval for exact plan version and hash.
- OpenClaw's default sandbox does not automatically isolate external ACP/OpenCode harnesses. OpenCode permission rules alone are not an OS sandbox.
- Run benchmarks that execute agent-generated code only in a disposable, independently isolated environment. Never grant a coding agent access to secrets, unrelated repos or operating-system administration.
- No cloud-model dependencies; no automatic model switching during an assignment.
- Preserve cross-platform .NET 10/React, SQLite, SignalR and worktree architecture. Prefer small changes and deterministic checks.
- The owner has authorized GitHub pushes, PR creation and merges for development of CodingAssistant in this conversation; that does not weaken the agent product's required approval policies.
