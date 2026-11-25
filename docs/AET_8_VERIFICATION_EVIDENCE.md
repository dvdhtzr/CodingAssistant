# AET-8 — Verification evidence foundation

The existing owner-confirmed, exact-scope Windows container-only smoke
lane now persists its admission, runner report, source fingerprint, scope
digest and plan digest. Restart interruption cannot manufacture a pass.
A new source or changed approval marks old evidence historical.
Contradictory runner results are blocked by the existing strict contract.
The evidence is always Verified=false. No additional runtime or execution
permission is created.

Static Web remains UNQUALIFIED in RuntimeProfileRegistry. The Windows fixed-fixture Chromium compatibility blocker was resolved on the
owner host (11/11 reported at 9d78e08, November 24, 2026). Separate
adversarial runtime qualification and independent functional tests remain required. CI's injected runner checks only
admission/persistence and does not execute or qualify generated source.

## Independent Calculation Journal contract

`tools/m4/calculation_journal_contract.mjs` defines seven mandatory functional checks and executes actual browser-driver operations outside the application page. The candidate app cannot supply the results through a same-origin harness, an HTML marker, or a claimed VERIFIED value. Each scenario starts a fresh private context; the persistence check requires the trusted driver to restart the browser process with the same profile. CI runs the same contract against the frozen working reference, inert reference, and a frozen forged-success script with otherwise working markup. These are trusted fixtures only, not generated code. The module neither launches a browser nor grants execution, and always returns evaluator observations only (runtimeQualified=false, generatedArtifactVerified=false). This is the functional contract foundation for #111, not completion of #76/#92 or a qualified runner. The owner-approved M4.7 seccomp and PID changes were restricted to its fixed compatibility probe; this commit does not extend that approval.
