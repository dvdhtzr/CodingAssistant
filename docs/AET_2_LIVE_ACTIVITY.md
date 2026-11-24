# AET-2 — Live engineering activity foundation

The existing SignalR progress hub now provides project-scoped activity hints.
The durable SQLite ledger records host observations from real saved
engineering, QA, repair, planning and policy events, plus separate agent
proposal metadata. Sequence numbers are contiguous within each project.
Events are written in the same transaction as the underlying record.

The owner-authenticated activity endpoint supports replay after a cursor.
Notifications are bounded, batched and best effort; reconnecting clients
subscribe before reconciling saved events. No model requests are added.

Engineering Studio displays a bounded live tail alongside the existing
workspace. Filters distinguish Team, Engineering and Diagnostics. Pausing
presentation does not pause work or block durable replay. Historical
records from before this migration remain visible in the original views.

This is operational telemetry, not a claim of streaming model output,
functional verification, a new execution authorization or completed
autonomous collaboration. Model-provider timing and actual independent
review will be extended at subsequent increments.
