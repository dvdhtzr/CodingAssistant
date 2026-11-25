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

The host-side candidate binder (`static_web_evaluation_candidate.mjs`) validates the exact current task/run/plan/specification/architecture/model/scope selection, the existing bounded ZIP parser, case-sensitive ordered file manifest, SHA-256 and UTF-8 byte counts, and the .NET-compatible source fingerprint. The candidate pins the evaluator source/version, mandatory cases and archive digest. It is immutable, emits no source contents and is explicitly non-executable. The model-free regression tests exercise changed revisions, scope, file bytes, traversal, symlink, extra file and forged status rejection; they are not the ten integrated OS security tests. This module is preparation for the host execution adapter, not an API admission route. The host must independently re-read current scope and obtain separate exact consent before any future execution.

## Trusted integrated qualification runner

The owner explicitly extended the M4.7 fixed seccomp variant and 256 PID ceiling to the trusted Calculation Journal and adversarial qualification only. `windows_aet8_trusted_qualification.mjs --run` stages exactly the four existing Git-blob-pinned reference files in memory, with no source path argument, host mount, model or generated archive. The same disposable guest must pass ten bounded OS negative cases, the M4.7 sandbox checks, seven external-driver functional cases, and inert and forged-receipt negative controls. Chromium uses a private inherited CDP pipe, not a TCP debugging listener or a page-controlled test receipt. No downloads, added capabilities, or changed container limits. The trusted driver sends actual mouse/key events and observes fixed DOM locators; browser restart preserves the private profile. Host validates exact result schema, evidence counts, pinned inputs and cleanup. This is a trusted integrated qualification only, NOT completion of #76 (exact product admission/cancel/recovery still required) or permission to execute generated source. CI tests the host policy, parser and pipe framing; real owner-host execution is required. A failed observation or cleanup blocks. Do not activate the planning registry or product acceptance based on these results.
