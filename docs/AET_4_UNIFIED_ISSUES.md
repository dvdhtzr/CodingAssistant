# AET-4 — Shared engineering issues and independent rechecks

- The shared issue projection groups identical findings on the same source
  revision and retains their actual lead/QA report references, severity,
  source location/evidence, owner triage, repair attempts and challenge history.
  Repeated reports cannot silently erase a previously reported issue.
- Engineer challenge messages bind the finding to an approved architecture
  decision. Only the delegated Technical Lead can record arbitration; it
  requires the current exact source, current review, latest challenge, and
  active policy. Decisions are revisioned. Major or critical findings cannot
  be deferred or treated as nonblocking improvements by this boundary.
- Both QA and Lead Software Engineer proposals use the same original
  repair_proposals, repair_triage_decisions and repair_cycles ledgers. The
  startup migration retains old QA IDs, triage and cycle references. The
  owner still classifies and confirms a specific source hash, one file and
  exact finding indices. A challenge or contrary arbitration cannot be
  bypassed by an owner repair request. The existing Software Engineer alone
  can write the cloned revision; no extra execution permission is created.
- An independent, finding-specific MAF recheck compares the exact original
  and candidate source, approved specification/architecture/plan, and issue
  evidence. The originating reviewer role and installed model are selected
  by the host. Results require bounded exact candidate source citations
  and are checked again transactionally before saving. New source, review,
  triage, challenge or decision evidence invalidates prior results.
- A static recheck can disposition a purely static architecture or
  maintainability issue, but a correctness, acceptance, security or
  performance finding remains awaiting qualified verification. Zero
  findings, a successful source repair or a static QA report never proves
  runtime behavior. Current and historical issues are inspectable by run ID.
- AET-6 will drive these application-only contracts through actual delegated
  Technical Lead decisions, repair scheduling and independent review order.
  AET-8 will add qualified deterministic build and functional evidence.
  No autonomous repair scheduling, generated execution or final delivery is
  authorized by this milestone's owner-facing API.
