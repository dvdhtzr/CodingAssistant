# Windows-local equivalent of the M0 Model-Free Checks workflow.
# Runs reviewed repository tests, not generated applications or real models.
[CmdletBinding(DefaultParameterSetName='Full')]
param(
    [Parameter(ParameterSetName='Full')][switch]$Full,
    [Parameter(ParameterSetName='Quick', Mandatory=$true)][switch]$Quick,
    [Parameter(ParameterSetName='Changed', Mandatory=$true)][switch]$Changed,
    [Parameter(ParameterSetName='Changed')][string]$Base = 'HEAD^',
    [switch]$StrictVersions
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$web = Join-Path $root 'src\CodingAssistant.Web'
$mode = $PSCmdlet.ParameterSetName
$effective = $mode
$results = New-Object 'System.Collections.Generic.List[object]'
$start = [DateTimeOffset]::UtcNow
$failed = $false
$commit = $null
$endCommit = $null
$clean = $false
$unchanged = $false
$ciVersionsMatch = $null
$nodeVersion = $null
$pythonVersion = $null
$dotnetVersion = $null
$oldLocation = Get-Location

function Invoke-Native {
    param([string]$Program, [string[]]$Arguments)
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Program exited with code $LASTEXITCODE"
    }
}

function Add-Check {
    param([string]$Name, [scriptblock]$Action)
    $results.Add([pscustomobject]@{
        Name=$Name; Status='NOT_RUN'; Seconds=0; Error=$null; Action=$Action
    })
}

function Read-Git {
    param([string[]]$Arguments)
    $lines = & git.exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Git could not read the requested repository state.' }
    return $lines
}

function Install-WebDependencies {
    # Full always uses the exact clean npm install from CI. The two shorter
    # modes reuse a previously successful lockfile-matched installation.
    $lock = (Get-FileHash -Algorithm SHA256 'package-lock.json').Hash
    $stamp = Join-Path 'node_modules' '.ca-local-validation-lock'
    if ($effective -eq 'Full' -or !(Test-Path $stamp) -or
        !(Test-Path 'node_modules\.bin\vite.cmd') -or
        (Get-Content -Raw $stamp).Trim() -ne $lock) {
        Invoke-Native 'npm.cmd' @('ci','--no-audit','--no-fund')
        Set-Content -LiteralPath $stamp -Value $lock -Encoding ASCII
    } else {
        Write-Host 'Reusing lockfile-matched npm dependencies (partial mode).'
    }
}

try {
    Set-Location -LiteralPath $root
    if ($env:OS -ne 'Windows_NT') { throw 'Use Windows for this script; Linux CI is separate.' }
    $commit = (Read-Git @('rev-parse','HEAD')).Trim()
    $initialStatus = @(Read-Git @('status','--porcelain=v1','--untracked-files=all'))
    $clean = $initialStatus.Count -eq 0
    if ($Changed) {
        $baseCommit = (Read-Git @('rev-parse','--verify','--end-of-options',("$Base" + '^{commit}'))).Trim()
        $paths = @(Read-Git @('diff','--name-only','--no-renames',$baseCommit,'--')) +
                 @(Read-Git @('ls-files','--others','--exclude-standard'))
        # Only an exclusively frontend change selects the shorter suite.
        # Shared, backend, security, ambiguous and empty changes use Full.
        $nonWeb = @($paths | Where-Object { $_ -notlike 'src/CodingAssistant.Web/*' })
        $effective = if ($paths.Count -gt 0 -and $nonWeb.Count -eq 0) { 'Web' } else { 'Full' }
        Write-Host "Changed base: $baseCommit; selected suite: $effective"
    }
    Write-Host "CodingAssistant local validation: $mode ($effective) | $commit"
    if (!$clean) { Write-Warning 'Dirty checkout: results describe local edits, not a clean commit.' }
    Write-Host 'Stop a running development server first to avoid build-output contention.'
    foreach ($tool in @('node.exe','npm.cmd','npx.cmd','python.exe','dotnet.exe','powershell.exe')) {
        if (!(Get-Command $tool -ErrorAction SilentlyContinue)) {
            throw "Missing prerequisite: $tool. See docs/LOCAL_VALIDATION.md."
        }
    }
    $nodeVersion = (& node.exe --version)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read Node version.' }
    $pythonVersion = (& python.exe --version)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read Python version.' }
    $dotnetVersion = (& dotnet.exe --version)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read .NET SDK version.' }
    Write-Host "$nodeVersion | $pythonVersion | .NET SDK $dotnetVersion"
    $ciVersionsMatch = ($nodeVersion -match '^v22\.' -and $pythonVersion -match '^Python 3\.13\.' -and
        $dotnetVersion -match '^10\.0\.')
    if (!$ciVersionsMatch) {
        $message = 'Tool versions differ from CI (Node 22, Python 3.13, .NET SDK 10.0).'
        if ($StrictVersions) { throw $message }
        Write-Warning "$message Continuing with installed versions; results are local compatibility evidence, not CI-version parity."
    }

    if ($effective -ne 'Web') {
        Add-Check 'Gateway and Windows tool syntax' {
            foreach ($file in @(
        'infra/m1/coding-runner.mjs',
        'infra/m1/coding-plugin/index.mjs',
        'infra/m1/coding-plugin/task-contract.mjs',
        'infra/m1/coding-plugin/safe-failure.mjs',
        'infra/m1/doctor-plugin/index.mjs',
        'tools/m4/windows_docker_guest.mjs',
        'tools/m4/windows_docker_qualification.mjs',
        'tools/m4/windows_scoped_workspace_guest.mjs',
        'tools/m4/windows_scoped_workspace_qualification.mjs',
        'tools/m4/windows_calculator_evaluator_guest.mjs',
        'tools/m4/windows_calculator_harness.js',
        'tools/m4/windows_calculator_evaluator_qualification.mjs',
        'tools/m4/windows_chromium_sandbox_guest.mjs',
        'tools/m4/windows_chromium_sandbox_qualification.mjs',
        'tools/m4/generated_zip_smoke_guest.mjs',
        'tools/m4/smoke_generated_zip.mjs'
            )) { Invoke-Native 'node.exe' @('--check',$file) }
        }
        Add-Check 'Model-free JavaScript security tests' {
            $files = @(
        'tests/test_m1_safe_acp_failure.mjs',
        'tests/test_m1_task_contract.mjs',
        'tests/test_m4_windows_docker_qualification.mjs',
        'tests/test_m4_windows_scoped_workspace.mjs',
        'tests/test_m4_windows_calculator_evaluator.mjs',
        'tests/test_m4_windows_chromium_sandbox.mjs',
        'tests/test_m4_generated_zip_smoke.mjs',
        'tests/test_m4_aet8_trusted_qualification.mjs'
            )
            Invoke-Native 'node.exe' (@('--test') + $files)
        }
        Add-Check 'Compile Python tools' {
            Invoke-Native 'python.exe' @('-m','compileall','-q','tools/m0','tools/m1','tools/m2','tools/m3','tools/m4','tools/mvp','benchmarks/acceptance','tests')
        }
    }
    if ($effective -eq 'Full') {
        Add-Check 'Python model-free unit tests' {
            Invoke-Native 'python.exe' @('-m','unittest','discover','-s','tests','-v')
        }
        Add-Check 'Windows launcher credential contract' {
            Invoke-Native 'powershell.exe' @('-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-File','tests/Verify-WindowsLauncher.ps1')
        }
    }
    Add-Check 'Install or reuse frontend dependencies' {
        Push-Location -LiteralPath $web
        try { Install-WebDependencies } finally { Pop-Location }
    }
    if ($effective -ne 'Quick') {
        Add-Check 'Production npm dependency audit' {
            Push-Location -LiteralPath $web
            try { Invoke-Native 'npm.cmd' @('audit','--omit=dev','--audit-level=high') }
            finally { Pop-Location }
        }
        Add-Check 'Full npm dependency audit' {
            Push-Location -LiteralPath $web
            try { Invoke-Native 'npm.cmd' @('audit','--audit-level=high') }
            finally { Pop-Location }
        }
    }
    Add-Check 'React workbench tests' {
        Push-Location -LiteralPath $web
        try { Invoke-Native 'npm.cmd' @('run','test:workbench') } finally { Pop-Location }
    }
    Add-Check 'TypeScript and React build' {
        Push-Location -LiteralPath $web
        try { Invoke-Native 'npm.cmd' @('run','build') } finally { Pop-Location }
    }
    if ($effective -ne 'Quick') {
        Add-Check 'Install or reuse trusted Chromium fixture browser' {
            Push-Location -LiteralPath $web
            try { Invoke-Native 'npx.cmd' @('playwright','install','chromium') }
            finally { Pop-Location }
        }
        Add-Check 'Chromium browser fixture tests' {
            Push-Location -LiteralPath $web
            try { Invoke-Native 'npm.cmd' @('run','test:browser') } finally { Pop-Location }
        }
    }
    if ($effective -eq 'Full') {
        Add-Check 'Windows static-web readiness guard' {
            Invoke-Native 'powershell.exe' @('-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-File','tests/Verify-WindowsStaticWebDoctor.ps1')
        }
        Add-Check 'M2 HTTP and SQLite smoke' {
            Invoke-Native 'dotnet.exe' @('run','--configuration','Release','--project','tests/CodingAssistant.Api.Smoke/CodingAssistant.Api.Smoke.csproj')
        }
        Add-Check 'Code Atlas smoke' {
            Invoke-Native 'dotnet.exe' @('run','--configuration','Release','--project','tests/CodingAssistant.CodeAtlas.Smoke/CodingAssistant.CodeAtlas.Smoke.csproj')
        }
        Add-Check 'Phase 2 runtime and decision persistence smoke' {
            Invoke-Native 'dotnet.exe' @('run','--configuration','Release','--project','tests/CodingAssistant.Phase2.Smoke/CodingAssistant.Phase2.Smoke.csproj')
        }
    } elseif ($effective -eq 'Quick') {
        Add-Check '.NET API Release build' {
            Invoke-Native 'dotnet.exe' @('build','--configuration','Release','src/CodingAssistant.Api/CodingAssistant.Api.csproj')
        }
    }

    foreach ($check in $results) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        Write-Host ("--- " + $check.Name) -ForegroundColor Cyan
        try {
            & $check.Action
            $check.Status = 'PASS'
        } catch {
            $check.Status = 'FAIL'
            $check.Error = $_.Exception.Message
            $failed = $true
            Write-Host $check.Error -ForegroundColor Red
        } finally {
            $check.Seconds = [Math]::Round($watch.Elapsed.TotalSeconds,2)
        }
        if ($failed) { break } # Never run dependent checks after a failed build.
    }

    $endCommit = (Read-Git @('rev-parse','HEAD')).Trim()
    $endStatus = @(Read-Git @('status','--porcelain=v1','--untracked-files=all'))
    $unchanged = $clean -and $endStatus.Count -eq 0 -and $commit -eq $endCommit
    if ($commit -ne $endCommit -or ($clean -and $endStatus.Count -ne 0)) {
        $failed = $true
        Write-Warning 'Checkout changed during validation; do not use this as clean-commit evidence.'
    }
} catch {
    $failed = $true
    Write-Host $_.Exception.Message -ForegroundColor Red
    $results.Add([pscustomobject]@{
        Name='Validation setup/state'; Status='FAIL'; Seconds=0; Error=$_.Exception.Message; Action=$null
    })
} finally {
    try {
        $reportDir = Join-Path $root 'artifacts\local-validation'
        New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
        $report = Join-Path $reportDir ($start.ToString('yyyyMMdd-HHmmss-fff') + '-' + $mode + '.json')
        $outcome = if ($failed) { 'FAIL' } elseif ($effective -eq 'Full') { 'WINDOWS_FULL_PASS' } else { 'PARTIAL_PASS' }
        $summary = [pscustomobject]@{
            Mode=$mode; EffectiveSuite=$effective; Commit=$commit; EndCommit=$endCommit
            CleanCommitEvidence=$unchanged; Outcome=$outcome; StartedUtc=$start.ToString('O')
            CIVersionsMatch=$ciVersionsMatch; StrictVersions=[bool]$StrictVersions
            ToolVersions=[pscustomobject]@{Node=$nodeVersion; Python=$pythonVersion; DotNet=$dotnetVersion}
            Seconds=[Math]::Round(([DateTimeOffset]::UtcNow-$start).TotalSeconds,2)
            Checks=@($results | Select-Object Name,Status,Seconds,Error)
        }
        $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $report -Encoding UTF8
        $results | Format-Table Name,Status,Seconds -AutoSize | Out-Host
        Write-Host "$outcome | $($summary.Seconds)s | report: $report"
        Write-Host 'Local evidence only: no Linux CI, live model or generated-source execution qualification.'
    } catch {
        $failed = $true
        Write-Host ('Could not write validation report: ' + $_.Exception.Message) -ForegroundColor Red
    }
    Set-Location -LiteralPath $oldLocation.Path
}
if ($failed) { exit 1 }
exit 0
