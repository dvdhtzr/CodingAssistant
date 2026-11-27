# Routine repair convergence — issue #210

Owner approved the architectural change to bounded source-only routine repairs
on the isolated AET branch. This does not activate permission for any local
project. The existing exact manual repair path remains the default.

## Increment implemented

- Versioned delegation optionally includes starting run ID, exact source
  fingerprint, explicit existing file paths and a finite budget (1–8 repairs
  per authorization, not a lifetime project cap). Scope is included in the
  policy digest and revalidated on approval. No new path or execution authority.
- A host-internal dispatch path uses the existing repair proposal, cycle,
  sole Software Engineer writer and collaboration continuation. It requires a
  current Technical Lead ImplementationDefect decision tied to the same
  delegation and review. The lead must cite actual source; QA requires an
  exact existing source excerpt and exact approved acceptance requirement.
  It does not fabricate owner triage or accept a public auto-approval flag.
- Durable admissions account for budget and same-policy source ancestry.
  Source assignment, commit and completion recheck current authority. Revocation
  prevents further candidate writes. Later routine repairs require independent
  prior-finding recheck evidence. Old source remains immutable.
- A grounded new blocking concern in candidate-introduced text escalates as
  a possible regression rather than continuing based on a finding count.
  This is conservative source-level evidence, not proof of runtime behavior.
- Team coordination UI exposes opt-in, explicit paths and budget, exact
  policy approval and revocation.
- Model-free acceptance scenario exercises two sequential distinct repairs:
  lead-origin then QA-origin, exact rechecks, no per-finding manual triage,
  original snapshot retention and an AwaitingVerification endpoint.

## Not qualified

CI on the exact branch head is required before any acceptance claim. The
scripted fixture does not demonstrate actual local-model quality, semantic
regression rejection or independent functional improvement. Candidate
source is still unverified and is not a certified replacement or delivery.
Runtime qualification and independent functional verification remain blocked
by #111/#76. The AET branch also predates several main-branch QA/repair
robustness changes; reconcile them carefully after obtaining a green
regression baseline. Do not merge AET into main without owner approval.

Next: complete exact-head CI, add negative authorization/revocation,
budget-exhaustion, regression and cancellation tests and clear current
candidate/accepted baseline UI; reconcile recent main fixes without changing
security constraints; exercise a disposable local real-model project and
then Idea Vault recovery. Escalate material blockers, not routine checks.
