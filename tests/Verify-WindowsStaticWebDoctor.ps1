# Windows-only contract smoke: the new Docker Desktop readiness check is
# deliberately non-executable and needs no Docker Desktop in CI.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '..\scripts\windows\Test-StaticWebDockerReadiness.ps1'
$source = [IO.File]::ReadAllText($script)
foreach ($required in @(
    'param([switch] $Inspect)',
    'if (-not $Inspect)',
    'Get-Command docker',
    'docker context show',
    'docker context inspect',
    'docker info',
    "StartsWith('npipe://'",
    'if ($info.OSType',
    'LOCAL_DOCKER_METADATA_READY',
    'executionEnabled: false'
)) {
    if (-not $source.Contains($required)) {
        throw ('Windows sandbox readiness guard removed: ' + $required)
    }
}
if ($source -match '(?im)^\s*&\s*docker\s+(?:run|pull|build|exec|rm)\b') {
    throw 'Read-only Windows sandbox inspection attempted a Docker mutation.'
}

$stdout = (& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $script 2>&1 |
    Out-String)
if ($LASTEXITCODE -ne 0 -or
    $stdout -notmatch 'WINDOWS_STATIC_WEB_SANDBOX: NOT_INSPECTED' -or
    $stdout -notmatch 'No application execution permission is granted') {
    throw 'No-argument Windows readiness check no longer fails closed.'
}
Write-Host 'WINDOWS_STATIC_WEB_DOCKER_PREFLIGHT_CONTRACT_PASS: no Docker guest or image operation.'
