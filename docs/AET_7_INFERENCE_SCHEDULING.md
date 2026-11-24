# AET-7 — Local inference scheduling foundation

All three C# local Ollama inference transports share a process-wide capacity
gate, including the streaming project draft fallback. Default capacity is
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

Durable work leases, restart reconciliation and safe overlapping work
remain to be integrated. Regression fixtures do not certify live hardware
performance or concurrency of externally launched Ollama requests.
