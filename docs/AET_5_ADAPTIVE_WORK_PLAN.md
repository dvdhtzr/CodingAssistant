# AET-5 — Adaptive work-plan foundation

The original owner-approved ImplementationPlan remains immutable. The
Technical Lead may revise a separate operational graph under a current
ScheduleWork delegation and exact source/plan binding. Each work package
inherits its approved objective, acceptance criteria, architecture and
verification strategy. Priorities, optional dependencies, suspension and
narrower safe paths are supported; mandatory dependencies and previously
narrowed scope cannot be removed or widened. Every graph is checked for
cycles.

An owner may explicitly select a specific current work-plan revision and
hash when admitting an ordinary engineering run. The host dispatches
ready packages using deterministic priority and dependency checks. One
source writer is still enforced. Between assignments, the Technical Lead
can update the active schedule without rewriting completed packages.
Suspended packages block dependent work without making speculative model
calls.

Assignment and completion receipts bind the operational revision and
actual source fingerprints. Scope is checked at the model boundary and
inside the source transaction. New or revoked delegation, stale
approvals or mismatched revision stop admission or writes safely. The
existing owner-authorized resume path retains completed work, with
dependency validation for adaptive schedules.

The current graph schedules existing approved work item IDs. Decomposing
an approved item into finer-grained executable work packages, additional
independent review checkpoints and autonomous orchestration are remaining
work; no source generation, execution or delivery privileges are granted
by the operational graph itself.
