# M4.5 — Task-bound Static Web verification scope preview

**Status: planning-only / NON-EXECUTABLE.** Owner confirmed real Windows
Docker Desktop M4.4 trusted reference evaluator GREEN after M4.2 18/18
and M4.3 17/17 guest + 9/9 independent host PASS. This remains a
trusted-fixture qualification, not a verified generated artifact.

## Why now

M4.4's working/broken calculator test is meaningful but its inputs
are pinned hand-authored fixture bytes. The next seam is to bind a
**real CodingAssistant Engineering run** to its current approved task,
specification, architecture, Technical Lead plan and actual saved source
file hashes before any execution capability is considered. Do not
translate the M4.4 PASS into permission to run model-generated files.

## What exists

The owner-authenticated, read-only endpoint:

```
GET /api/tasks/{taskId}/engineering-runs/{runId}/verification-scope
```

reads the existing engineering snapshot and independently compares it to
the latest run, approved specification, approved Technical Lead plan,
and owner-approved architecture decisions, including the Static Web
runtime selection. A successful **scope candidate** includes:

- Task and run IDs, specification version/hash, plan revision/hash,
  architecture hash, chosen local model
- Sorted allowlisted source file **paths, SHA-256 and UTF-8 byte counts**
- Full engineering-source fingerprint and a deterministic SHA-256
  over the complete, versioned, task/plan/model/source descriptor
- `status: "SCOPE_CANDIDATE"`, but
  `executionEnabled: false` and
  `verificationStatus: "GENERATED_NOT_TESTED"`

The preview **never** returns file bodies, calls a model, writes a
workspace, starts Docker, invokes a browser, or grants execution.
The digest is **not** an authorization token.

Any stale/nonlatest/unfinished run, changed approval, non-static-web
architecture selection, unsafe or colliding relative paths, forbidden
extension, excessive source bytes, incorrect saved content hash,
missing root index.html or current static preflight findings yields
`status: "BLOCKED"` and fixed-size blocker codes. The existing
engineering ZIP remains a source-only, unverified artifact.

The preview is intended as an immutable foundation for a *separate*
owner-approved executable scope and independently secured verifier.
A scope candidate MUST NOT be equated with approved executable scope
and MUST NOT directly authorize Docker guest admission.

## CI and outstanding safety gates

Real Windows/Ubuntu API+SQLite smoke tests exercise a positive
snapshot, deterministic digest, altered plan, nonlatest/unfinished
runs, superseded approvals, file hash mutation, case-folded duplicates,
traversal, executable script extension, missing entrypoint, disconnected
JS and changed runtime selection. No generated source is executed.

Before enabling any model-generated source in a browser:

1. Qualify a Chromium profile suitable for **untrusted** source, without
   the trusted-fixture-only `--no-sandbox` exception.
2. Build a reviewed, exact owner-confirmed execution request whose
   entire source/workspace/image/plan scope matches the preview, and
   recheck every byte at dispatch.
3. Complete issue #76 ten integrated adversarial cases, child
   restrictions, network/Git denial, cancellation and crash recovery.
4. Complete issue #92 actual task-scoped project admission, without
   ever mounting an original user repository.
5. Implement #111 independent source-specific tests/feedback, bounded
   engineer repair (at most two retries), and truthful VERIFIED or
   TESTS_FAILED evidence.

M4.5 requires **no new owner-side terminal command**. Its endpoint is
available in the Windows application after GitHub merge and app restart.
