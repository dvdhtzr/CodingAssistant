# M0 milestone status — November 18, 2026

**Status: YELLOW / IN PROGRESS (not GREEN).**

Implemented: initial model-free evaluation tooling; host/Ollama inventory; single tool-call probe; isolated-host-only benchmark runner; three fixed fixtures; independent accept/reject rules; Linux and Windows usage guidance.

**Verification:** seven model-free unit tests passed in the assistant's build environment. Python syntax checks passed. Actual Dell/Windows Ollama/OpenCode model benchmarks, .NET fixture compilation, React runtime verification and mandatory M1 security tests have **not** been performed.

**Remaining M0 gates:** independently capture installed versions and model digests on both machines; prove OpenCode discovers and invokes Ollama for a coding session; run each of three assignments three times under external OS isolation; independently review unsafe operations, false completion claims and recovery; select qualified primary and bounded Dell fallback.

**Safety constraint:** M0 benchmark has no independently enforced execution boundary. Do not use it on an ordinary workstation with personal data or credentials.

**GitHub status:** PR #1 merged into main on November 18, 2026; Ubuntu and Windows model-free CI jobs passed. A follow-up PR adds a local OpenCode model-discovery probe.

Recommendation: keep M1 disabled until M0 model acceptance is proven. Do not equate unit tests with model qualification.

**Operator update:** Dell prerequisite checks were reported green by the owner. Full raw machine output and independent benchmark evidence have not been received, so this is not an M0 exit pass. M0.3 discovery is a separate check.

**Operator M0.3 report (Nov 18):** Dell `qwen3.5:4b` OpenCode discovery PASS on all five checks, including exact installed model ID and tool capability. Independent coding benchmarks have **not** run. Next: M0.4 rootless isolation negative preflight before any generated code execution.

**Owner-reported M0.4 evidence (Nov 18):** rootless Podman isolation PRECHECK_PASS on Dell, all seven checks passed: no external networking, capabilities or new privileges, root filesystem read-only, no unmounted host file access, unprivileged identity, and writable disposable workspace. This is a **preflight only**; OpenCode, inference relay, independent safety tests and M0 benchmark are still pending.

**Next:** M0.5 private AF_UNIX Ollama metadata bridge and loopback feasibility. No AI coding is authorized by the M0.4 result.

**Owner-reported M0.5 evidence (Nov 18):** Local Ollama metadata bridge PRECHECK_PASS on Dell; all seven checks passed and container loopback TCP was available. The container did not run an autonomous coding agent or model inference.

**M0.6 next:** prove model-pinned, bounded Qwen inference through isolated loopback HTTP → AF_UNIX bridge without exposing host/public network. This does not authorize OpenCode or generated-code execution. See `docs/M0_PINNED_INFERENCE.md`.

**Owner-reported M0.6 evidence (Nov 18):** `M0.6 ISOLATED INFERENCE INFERENCE_PASS: qwen3.5:4b`. All eight checks passed on Dell: model-pinned inference, host sentinel hidden, loopback-to-Unix metadata, outbound TCP denial, read-only rootfs, non-root identity, denied unsupported endpoint, and denied wrong model. This was one fixed inference prompt, **not OpenCode or generated code execution**.

**Next M0.7 gate:** build a local OpenCode 2.3.0 + .NET 10 + Node 22 + Git toolchain image with a restricted build context; verify CLI startup in rootless Podman with no network and no source mounts. Actual autonomous coding remains **disabled**.

**Operator-reported M0.7 initial attempt (Nov 18):** container image build appears to have completed, but the local verifier rejected an unprefixed Podman image ID before toolchain execution. This is a verifier-format defect, **not evidence of toolchain failure**. Fix tested in PR; operator should rerun without `--build` after merge.
