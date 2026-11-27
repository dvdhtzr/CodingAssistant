# AET-1 — Agent communication and delegated authority

This is an additive foundation on the approved AET branch. Existing MAF agents,
owner approval endpoints and source/repair/execution admission rules are preserved.

- Host-observed assignment and completion receipts are recorded atomically with
  source/run changes. They reference the existing approved plan, specification,
  architecture and QA-compatible source fingerprint.
- Separate durable agent communication records hold bounded typed proposals with
  trusted host-origin identity, assignment correlation, revision-bound evidence,
  idempotency keys, and reply limits. Inbox/outbox queries are owner-authenticated.
  A proposal does not execute a transition or constitute verified evidence.
- Owner-only delegation management uses a versioned exact-plan hash, explicit
  approval and revocation. There is no default delegation, execution, deployment,
  Git, or scope-expansion operation. Future autonomous transitions must enforce
  this gate AND existing operation-specific admission rules inside the same
  transaction. A new approved plan requires fresh delegation; adaptive work-plan
  revisions must stay within the approved scope (AET-5).
- No new agent invocation is wired to publication yet; injected tests exercise
  the application boundary. AET-3 introduces the independent reviewer and AET-6
  activates collaboration. There is deliberately no public message-send route.
- Existing databases receive additive tables. Project deletion cascades records.
- AET-2 will deliver revision-aware activity via SignalR and durable replay,
  with no raw prompts, secrets, private reasoning or fabricated progress.
