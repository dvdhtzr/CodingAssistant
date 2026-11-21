# UX-6C — Windows-first Chromium regression harness and sandbox readiness

**Status:** Implemented model-free browser regression harness; **Windows Docker
Desktop sandbox and generated-project execution remain UNQUALIFIED**.

## Windows-first working style

Windows (Visual Studio, local Ollama and Mission Control) is the primary
developer experience. No Dell laptop acceptance is required to use the
existing source-only product or to develop these trusted browser fixtures.
Docker Desktop with WSL2 Linux containers is the future Windows isolation
candidate, **not currently an approved executor**.

This increment adds real Chromium browser interaction tests to **both**
\`windows-latest\` and \`ubuntu-latest\` CI jobs, using trusted fixed fixtures
in \`src/CodingAssistant.Web/tests/browser/fixtures/calculation-journal/\`.
The original source failure pattern from #107 is recreated in \`broken.html\`
and \`broken.js\`: a placeholder inline script, an unlinked JavaScript file,
and JS selectors missing from the HTML. The negative test confirms that a
rendered calculator is inert when clicked. A separate clearly labeled
**hand-authored reference** \`working.html\` + \`working.js\` must pass:

- Mouse: 2 + 3 = 5
- Keyboard: 12.5 ÷ 2 = 6.25
- Decimal: 0.1 + 0.2 = 0.3
- Backspace, divide-by-zero and recovery
- Back recall, History list selection
- History survives a page reload **and real Chromium process restart** using
  one isolated temporary browser profile.

These are **test-harness qualification fixtures**, NOT output autonomously
generated or repaired by CodingAssistant. CI runs them against a Vite server
bound to 127.0.0.1 on an ephemeral port. No model is invoked, no arbitrary
user source is loaded, and no shell/container privileges are granted to the
app. The CI browser result is a real test of trusted fixtures on each CI OS,
but **not** a containment test for untrusted generated code.

## Optional local Docker Desktop readiness inspection

From a regular Windows PowerShell prompt at the repository root:

\`\`\`powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\windows\Test-StaticWebDockerReadiness.ps1 -Inspect
\`\`\`

This inspection is read-only. It requires a **local named-pipe Docker
context**, Docker Desktop Linux daemon, and advertised process/memory
constraints; remote TCP/SSH Docker contexts are rejected. It makes **no
container, image pull, socket mount, privilege change, external request,
browser launch, or task-specific approval**. A missing Docker Desktop or a
Windows-container daemon returns \`BLOCKED\` with an explanation.

A successful result is only \`LOCAL_DOCKER_METADATA_READY\`, meaning *the
machine appears able to support a future qualification*. This is not a
security qualification; it never changes \`static-web\`'s
\`qualificationStatus=UNQUALIFIED\` or \`executableViaRegistry=false\`.

Read-only/no-argument behavior is tested on Windows CI without requiring
Docker Desktop on GitHub runners.

## Remaining qualification before generated code can run on Windows

1. Independently pin a locally cached Node + Chromium evaluator image
   by immutable digest, with its exact Playwright package/browsers, licenses,
   security features and limits reviewed. No automatic image pull when
   running generated code.
2. Verify a *real Windows Docker Desktop/WSL2 guest*, file/host/secret
   isolation, no host mounts except an exact disposable source snapshot,
   no network/Git/child process escapes, and cleanup; retain host evidence.
3. Bind an immutable, separately owner-approved task/plan/snapshot, source
   digest, runtime image/evaluator digests, files, test plan and capabilities.
   Keep the untrusted browser/evaluator separate from the control plane.
4. Finish #76's **integrated** 10 adversarial cases, #92 task scope,
   cancellation/crash reconciliation, #111 test evidence and ≤2 repairs.
5. Only after independent Windows-specific qualification allow source from
   CodingAssistant itself to be mounted, executed and considered for a
   \`VERIFIED\` outcome. A browser check passing in CI on trusted fixtures
   is never substitute evidence.

## Official references

- [Docker Desktop Windows and WSL2](https://docs.docker.com/desktop/features/wsl/)
- [Docker Desktop Windows installation](https://docs.docker.com/desktop/setup/install/windows-install/)
- [Playwright browser container safety](https://playwright.dev/docs/docker)
- [Docker container security/run options](https://docs.docker.com/reference/cli/docker/container/run)

The dedicated static-web profile registry remains planning-only and is
intentionally unchanged by this PR.
