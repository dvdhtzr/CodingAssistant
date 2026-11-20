# One-click Windows development launcher.
# Never store the owner token in a file, URL, argument or source-controlled config.
$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$webRoot = Join-Path $repoRoot 'src\CodingAssistant.Web'
$apiProject = Join-Path $repoRoot 'src\CodingAssistant.Api\CodingAssistant.Api.csproj'
$healthUrl = 'http://127.0.0.1:5098'
. (Join-Path $PSScriptRoot 'OwnerToken.ps1')

try {
    Set-Location -LiteralPath $repoRoot

    if (-not (Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue)) {
        throw '.NET 10 SDK was not found. Install it, then start CodingAssistant again.'
    }
    if (-not (Get-Command 'npm.cmd' -ErrorAction SilentlyContinue)) {
        throw 'Node.js/npm was not found. Install Node.js, then start CodingAssistant again.'
    }
    if (-not (Test-Path -LiteralPath $apiProject)) {
        throw 'Could not find CodingAssistant.Api.csproj in this checkout.'
    }

    # Do not print a brand-new token for a *different* already-running API:
    # the browser would reject it, and that is the problem this fixes.
    $listeners = @(Get-NetTCPConnection -LocalPort 5098 -State Listen -ErrorAction SilentlyContinue)
    if ($listeners.Count -gt 0) {
        throw 'Port 5098 is already occupied. Stop the earlier CodingAssistant server (Ctrl+C) and relaunch.'
    }

    Write-Host ''
    Write-Host 'CodingAssistant - Windows launcher' -ForegroundColor Cyan
    Write-Host 'Building Mission Control UI from this checkout...' -ForegroundColor Gray

    # First launch installs frontend dependencies; later launches work offline
    # with the installed packages. A failed build gets one dependency refresh.
    $vite = Join-Path $webRoot 'node_modules\.bin\vite.cmd'
    if (-not (Test-Path -LiteralPath $vite)) {
        & npm.cmd --prefix $webRoot install --no-audit --no-fund --prefer-offline
        if ($LASTEXITCODE -ne 0) {
            throw 'Frontend dependency installation failed.'
        }
    }
    & npm.cmd --prefix $webRoot run build
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Refreshing frontend dependencies after a failed build...' -ForegroundColor Yellow
        & npm.cmd --prefix $webRoot install --no-audit --no-fund --prefer-offline
        if ($LASTEXITCODE -ne 0) {
            throw 'Frontend dependency refresh failed.'
        }
        & npm.cmd --prefix $webRoot run build
        if ($LASTEXITCODE -ne 0) {
            throw 'Frontend build failed. See the npm errors above.'
        }
    }

    # The token is fresh for *this* process only and is not saved anywhere.
    $env:CODINGASSISTANT_OWNER_TOKEN = New-CodingAssistantOwnerToken
    $env:CODINGASSISTANT_ENABLE_LOCAL_EXECUTION = '0'

    Write-Host ''
    Write-Host '=============================================================' -ForegroundColor Cyan
    Write-Host 'COPY THIS 64-CHARACTER OWNER TOKEN:' -ForegroundColor Yellow
    Write-Host $env:CODINGASSISTANT_OWNER_TOKEN -ForegroundColor Green
    Write-Host '=============================================================' -ForegroundColor Cyan
    try {
        Set-Clipboard -Value $env:CODINGASSISTANT_OWNER_TOKEN
        Write-Host 'The token was also copied to your clipboard.' -ForegroundColor Green
    }
    catch {
        Write-Host 'Clipboard unavailable: select and copy the green token above.' -ForegroundColor Yellow
    }
    Write-Host ''
    Write-Host "Open $healthUrl and paste the token into Owner session." -ForegroundColor Cyan
    Write-Host 'Keep this terminal open while using CodingAssistant. Press Ctrl+C to stop.' -ForegroundColor Gray
    Write-Host 'Windows autonomous code execution remains DISABLED.' -ForegroundColor Yellow
    Write-Host ''

    # Run in this console so Ctrl+C stops the server without detaching a
    # privileged or orphaned process. No bearer token appears in arguments.
    & dotnet.exe run --no-launch-profile --project $apiProject
    if ($LASTEXITCODE -ne 0) {
        throw "CodingAssistant exited with code $LASTEXITCODE."
    }
}
catch {
    Write-Host ''
    Write-Host ("Could not launch CodingAssistant: " + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
finally {
    Remove-Item Env:CODINGASSISTANT_OWNER_TOKEN -ErrorAction SilentlyContinue
    Remove-Item Env:CODINGASSISTANT_ENABLE_LOCAL_EXECUTION -ErrorAction SilentlyContinue
}
