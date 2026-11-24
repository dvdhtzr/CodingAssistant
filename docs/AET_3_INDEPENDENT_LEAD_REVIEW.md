# AET-3 — Independent Lead Software Engineer

The sixth agent is a distinct Microsoft Agent Framework ChatClientAgent, not a prompt alias for QA or the Software Engineer. Its only work capability is static code review and communication. It uses an owner-selected installed local Ollama model; the provisional default is qwen3-coder:30b. This is a choice, not a benchmark result.

The reviewer receives the current approved plan, architecture, specification, and immutable generated source. The existing bounded read-only Code Atlas gateway provides at most two additional observations per file. Review output is a strict structured disposition and findings; each finding must point to an exact saved file, 1-based line, and source substring. Critical and Major findings cannot coexist with an approved disposition. Static review approval is not owner approval, a build, a runtime test, or permission to alter source.

An owner-authenticated API admits review only for the latest generated source with matching current approvals and an installed model. The independent ledger rechecks the source fingerprint and plan at admission, each saved report, and completion. Results become stale when the underlying source or approvals change; cancellation and restart interruption retain evidence. No generated code is executed. The Engineering Studio exposes the sixth specialist and the actual saved review state.

This milestone intentionally does not automatically start QA, repair findings, or supersede owner approvals. Unified issue handling and autonomous work scheduling belong to AET-4 onward. Regression tests use injected local model fixtures; real model behavior is separately qualified.
