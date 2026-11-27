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

## Remote validation during the experiment

The M0 Model-Free Checks push trigger excludes only
feature/autonomous-engineering-team. main, other feature branches and
pull requests into main retain their original behavior. The separate AET-8
trusted sandbox workflow retains its security-sensitive triggers. Existing
in-flight checks are not canceled.

To run the full hosted matrix manually: GitHub Actions > M0 Model-Free Checks
> Run workflow > feature/autonomous-engineering-team (or
`gh workflow run m0-tests.yml --ref feature/autonomous-engineering-team`).
Run it at milestones and before any merge. Required PR checks and Linux
acceptance are not waived by a local Windows pass. Restore automatic pushes
by reverting the standalone workflow-trigger commit (do not revert the
local script), or remove its single negative branch filter.
