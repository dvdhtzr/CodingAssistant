# Runs in Windows PowerShell 5.1 in GitHub Actions. Never prints a token.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\windows\OwnerToken.ps1')

$seen = New-Object 'System.Collections.Generic.HashSet[string]'
for ($i = 0; $i -lt 20; $i++) {
    $token = New-CodingAssistantOwnerToken
    if ($token -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Windows launcher token is not exactly 64 lowercase hex characters.'
    }
    if (-not $seen.Add($token)) {
        throw 'Repeated owner token returned by cryptographic generator.'
    }
}
$launcher = Get-Content -Raw (Join-Path $PSScriptRoot '..\scripts\windows\Start-CodingAssistant.ps1')
$wrapper = Get-Content -Raw (Join-Path $PSScriptRoot '..\Start-CodingAssistant.cmd')
if (-not $launcher.Contains('New-CodingAssistantOwnerToken') -or
    -not $launcher.Contains('Write-Host $env:CODINGASSISTANT_OWNER_TOKEN') -or
    -not $launcher.Contains('Set-Clipboard -Value $env:CODINGASSISTANT_OWNER_TOKEN') -or
    -not $launcher.Contains("CODINGASSISTANT_ENABLE_LOCAL_EXECUTION = '0'") -or
    -not $wrapper.Contains('scripts\windows\Start-CodingAssistant.ps1')) {
    throw 'Windows launcher is missing a required token display, clipboard or safety step.'
}
Write-Host 'WINDOWS_LAUNCHER_TOKEN_PASS: 20 unique 64-character tokens; display, copy and execution-off contract present.'
