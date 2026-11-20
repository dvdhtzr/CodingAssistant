# Runs in Windows PowerShell 5.1 in CI. Never prints or logs a token.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\windows\OwnerToken.ps1')

$seen = New-Object 'System.Collections.Generic.HashSet[string]'
for ($i = 0; $i -lt 20; $i++) {
    $candidate = New-CodingAssistantOwnerToken
    if ($candidate -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Windows token is not 64 lowercase hex characters.'
    }
    if (-not $seen.Add($candidate)) {
        throw 'Repeated random owner token.'
    }
}

$isolated = Join-Path ([IO.Path]::GetTempPath()) (
    'ca-win-owner-' + [Guid]::NewGuid().ToString('N'))
try {
    $first = Get-CodingAssistantOwnerToken -PrivateDataDirectory $isolated
    if ($first -cnotmatch '^[0-9a-f]{64}$') {
        throw 'First persisted owner credential malformed.'
    }
    $path = Join-Path $isolated 'owner-token.dpapi'
    if (-not [IO.File]::Exists($path)) {
        throw 'Windows credential was not persisted for Visual Studio F5.'
    }
    $file = [IO.File]::ReadAllText($path)
    if ($file -match [Regex]::Escape($first) -or $file.Length -lt 32) {
        throw 'Saved owner token was not encrypted with DPAPI.'
    }
    $second = Get-CodingAssistantOwnerToken -PrivateDataDirectory $isolated
    if ($first -cne $second) {
        throw 'Second launcher launch changed the saved owner credential.'
    }

    # The implicit default and an explicit override must resolve to the same
    # saved credential; this is what the .NET API reads in a VS F5 session.
    $oldDirectory = $env:CODINGASSISTANT_DATA_DIR
    try {
        $env:CODINGASSISTANT_DATA_DIR = $isolated
        $implicit = Get-CodingAssistantOwnerToken
        if ($implicit -cne $first) {
            throw 'Environment data directory did not resolve saved credential.'
        }
    }
    finally {
        $env:CODINGASSISTANT_DATA_DIR = $oldDirectory
    }

    # A corrupt encrypted blob must NEVER silently generate a different token.
    [IO.File]::WriteAllText($path, 'invalid-base64!')
    $replaced = $false
    try {
        $null = Get-CodingAssistantOwnerToken -PrivateDataDirectory $isolated
        $replaced = $true
    }
    catch { }
    if ($replaced -or [IO.File]::ReadAllText($path) -ne 'invalid-base64!') {
        throw 'Corrupted owner credential was silently replaced.'
    }

    $launcher = Get-Content -Raw (
        Join-Path $PSScriptRoot '..\scripts\windows\Start-CodingAssistant.ps1')
    $wrapper = Get-Content -Raw (
        Join-Path $PSScriptRoot '..\Start-CodingAssistant.cmd')
    $copyScript = Get-Content -Raw (
        Join-Path $PSScriptRoot '..\scripts\windows\Copy-CodingAssistant-Token.ps1')
    $copyWrapper = Get-Content -Raw (
        Join-Path $PSScriptRoot '..\Copy-CodingAssistant-Token.cmd')
    if (-not $launcher.Contains('Get-CodingAssistantOwnerToken') -or
        -not $launcher.Contains('Set-Clipboard -Value $env:CODINGASSISTANT_OWNER_TOKEN') -or
        $launcher.Contains('Write-Host $env:CODINGASSISTANT_OWNER_TOKEN') -or
        -not $launcher.Contains("CODINGASSISTANT_ENABLE_LOCAL_EXECUTION = '0'") -or
        -not $wrapper.Contains('scripts\windows\Start-CodingAssistant.ps1') -or
        -not $copyScript.Contains('Get-CodingAssistantOwnerToken') -or
        -not $copyScript.Contains('Set-Clipboard') -or
        -not $copyWrapper.Contains('Copy-CodingAssistant-Token.ps1')) {
        throw 'Windows startup lost shared credential, clipboard or execution-off contract.'
    }

    Write-Host 'WINDOWS_LAUNCHER_TOKEN_PASS: random tokens, DPAPI persistence, restart reuse, corruption fail-closed, clipboard and execution-off.'
}
finally {
    if ([IO.Directory]::Exists($isolated)) {
        [IO.Directory]::Delete($isolated, $true)
    }
}
