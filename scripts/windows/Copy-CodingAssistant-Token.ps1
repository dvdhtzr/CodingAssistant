# For Visual Studio F5 sessions: copy the *same* owner credential as the
# one-click launcher, without printing it to a terminal or changing the API.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OwnerToken.ps1')
try {
    $credential = Get-CodingAssistantOwnerToken
    Set-Clipboard -Value $credential
    Write-Host 'CodingAssistant owner token copied to clipboard. Paste in Owner session.' -ForegroundColor Green
}
catch {
    Write-Host ('Cannot copy CodingAssistant owner token: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
finally {
    Remove-Variable credential -ErrorAction SilentlyContinue
}
