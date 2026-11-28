# Local-first Windows validation

Use from a clean checkout of the repository, with the running development
server stopped (the build may replace its output files). The checks use
trusted repository fixtures, not the owner's projects, live Ollama or arbitrary
generated code. Node 22, Python 3.13 and .NET 10 SDK match CI and are recommended.
Installed tools with different versions are allowed by default, with an explicit
warning and actual versions recorded in the JSON report. Use -StrictVersions
to require matching CI versions. Passing local tests with different versions
does not establish CI-version parity. Missing tools and actual test failures
still fail validation. npm/NuGet restore, npm audit and first-time Chromium
installation may require network access.

```powershell
.\Test-CodingAssistant.ps1 -Full
.\Test-CodingAssistant.ps1 -Full -StrictVersions
.\Test-CodingAssistant.ps1 -Quick
.\Test-CodingAssistant.ps1 -Changed
.\Test-CodingAssistant.ps1 -Changed -Base origin/main
```

Default is Full. If local PowerShell execution policy blocks the script, use
`powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Test-CodingAssistant.ps1 -Full`
for that process only; do not change machine-wide policy.

- **Full:** all Windows commands from M0 Model-Free Checks, including security
  tests, Python, both npm audits, both UI test suites, Windows launcher/doctor
  checks, and the three .NET smoke suites. Uses clean npm ci and caches
  downloaded packages and browser binaries. Does not run Linux-only publish,
  artifact upload, release or separate sandbox workflows.
- **Quick:** model-free JavaScript security/syntax, Python compile, frontend
  unit tests and build, and API Release build. No Python unit tests, audits,
  Chromium, or .NET smoke execution.
- **Changed:** compares working tree to the supplied base (default HEAD^),
  including nonignored untracked files. Only exclusively frontend changes
  select the partial Web suite (dependency audits, workbench, build,
  Chromium fixtures). Any other or empty change set runs Full. Use Full for
  milestones; partial results are not interchangeable with full regressions.

The first failure stops dependent checks; the terminal summary reports PASS,
FAIL or NOT_RUN, and a JSON report under ignored artifacts/local-validation/
records timings, revision, cleanliness and outcome. Exit 1 means failure.
A dirty checkout is not clean-commit evidence. Run on an unchanged checkout.
The script does not push, modify CI settings or confer execution approval.

## Local-first development and merge policy

Feature branch pushes and ordinary pull request updates do not automatically
run hosted CI. Use Quick for routine development and Full for milestone and
pre-merge validation. Report the exact tested commit and clean working tree.
Batch implementation commits between meaningful checkpoints. Local Windows
regressions do not replace Linux-specific acceptance or native execution
qualification. No security or owner approval boundary changes.

Hosted checks run on main pushes; M0 Model-Free Checks also retains its
merged-PR fallback for GitHub App merges. Manual workflow dispatch remains
available for exceptional cross-platform and specialized runtime checks.
The specialized workflows now run on all main pushes rather than filtered
paths. Branch protection, if it requires pre-merge hosted checks, must be
reconciled by the repository owner; do not bypass required checks.

Before merging, require explicit owner authorization and successful full
local validation against the exact candidate commit. If the base changes,
reconcile and revalidate before merging. Inspect main CI after the merge.
Include this policy in all future CodingAssistant development handoffs.

## Optional real planning transport probe

From the repository root, with the development server stopped:

~~~powershell
$env:CODINGASSISTANT_TEST_NODE_EXE = (Get-Command node.exe -ErrorAction Stop).Source
dotnet run --project tests/CodingAssistant.Api.Smoke/CodingAssistant.Api.Smoke.csproj
Remove-Item Env:CODINGASSISTANT_TEST_NODE_EXE
~~~

Expect `EVALUATION_PLANNING_NODE_TRANSPORT_PASS` and the normal M2 smoke
pass. The opt-in tests execute only trusted inline Node transport fixtures
and the real bridge's missing-provenance rejection; no Docker metadata
query, browser, generated code, host enrollment or execution permission
is involved. The test variable is not production configuration.
