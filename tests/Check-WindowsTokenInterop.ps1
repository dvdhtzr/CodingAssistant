# Run only from Windows .NET model-free smoke tests. No credential output.
param(
    [Parameter(Mandatory=$true)]
    [string]$PrivateDataDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\windows\OwnerToken.ps1')
$expected = $env:CODINGASSISTANT_TOKEN_TEST_SHA
if ($expected -cnotmatch '^[0-9a-f]{64}$') {
    throw 'Windows interop test digest is missing.'
}
$secret = Get-CodingAssistantOwnerToken -PrivateDataDirectory $PrivateDataDirectory
$hasher = [Security.Cryptography.SHA256]::Create()
try {
    $actual = ([BitConverter]::ToString($hasher.ComputeHash(
        [Text.Encoding]::ASCII.GetBytes($secret))) -replace '-', '').ToLowerInvariant()
    if ($actual -cne $expected) {
        throw 'PowerShell could not read the same Windows owner credential as .NET.'
    }
}
finally {
    $hasher.Dispose()
    Remove-Variable secret -ErrorAction SilentlyContinue
}
Write-Host 'WINDOWS_DPAPI_INTEROP_PASS: C# and Windows PowerShell read one persistent owner credential.'
