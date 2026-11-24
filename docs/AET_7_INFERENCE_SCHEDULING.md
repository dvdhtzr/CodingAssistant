# AET-7 — Local inference scheduling foundation

All four C# local Ollama inference transports share a process-wide capacity
gate, including the streaming project draft and static HTML preview fallbacks. Default capacity is
one concurrent actual request. An operator may set
CODINGASSISTANT_INFERENCE_CAPACITY to an integer from 1 to 4 before startup;
larger settings are not hardware qualification. The queue is bounded,
cancellable, and has a bounded wait. Each granted lease releases exactly
once, including failed or rejected outputs. The streaming adapter also
bounds the complete response body, not only response headers.

The gate regulates this API process only, not other Ollama clients or
separate application processes. It neither grants source write permission
nor changes model selection or execution authorization. MAF queue events
are actual admission waits, not simulated activity. The owner-only API
exposes capacity, active requests and waiting counts.

Scoped MAF provider work has durable lease receipts written atomically
with actual request-start and terminal activity. Startup marks unresolved
provider leases interrupted; late completion is rejected and no inference
is automatically replayed. Existing source checkpoints remain authoritative.
Safe overlapping project work and broader work-lease integration remain.
The gate is process-local; other applications and separate API processes
are outside its capacity guarantee. Fixtures do not certify live hardware.

An interrupted coordination session can be explicitly resumed by its owner
with the same exact run/source/plan/policy binding. Resume appends a durable
checkpoint marker. Unlinked intents are not completed work; linked completed
review receipts are not dispatched again. A linked interrupted review can
be attempted once per new explicit resume. A generated repair whose
continuation was never admitted has a separate exact-source resume endpoint;
the unique durable child link prevents duplicate admission. No owner consent
or generated execution permission is inferred from a restart.
