# Windows-first Static Web sandbox preflight.
# READ ONLY. This script never pulls images, starts containers, opens a
# network listener, executes projects or marks runtime profiles qualified.
# Windows PowerShell 5.1 compatible.
[CmdletBinding()]
param([switch] $Inspect)

$ErrorActionPreference = 'Stop'
if (-not $Inspect) {
    Write-Host 'WINDOWS_STATIC_WEB_SANDBOX: NOT_INSPECTED'
    Write-Host 'Use -Inspect to check local Docker Desktop Linux-engine readiness.'
    Write-Host 'No application execution permission is granted.'
    exit 0
}

function Block([string] $Reason) {
    Write-Host 'WINDOWS_STATIC_WEB_SANDBOX: BLOCKED'
    Write-Host ('  reason: ' + $Reason)
    Write-Host '  qualified: false; executionEnabled: false'
    exit 2
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    Block 'Windows PowerShell on the Windows host is required.'
}
$docker = Get-Command docker -ErrorAction SilentlyContinue
if ($null -eq $docker -or $docker.CommandType -ne 'Application') {
    Block 'Docker CLI not found. Install/start Docker Desktop with Linux containers and WSL2.'
}

try {
    # Collect only JSON metadata, never environment, volume or secret contents.
    # Docker Desktop may be stopped or configured to point to a remote daemon.
    $context = (& docker context show 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($context)) {
        Block 'Docker CLI context could not be inspected.'
    }
    $endpoint = (& docker context inspect $context --format '{{json .Endpoints.docker.Host}}' 2>$null |
        Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($endpoint)) {
        Block 'Docker engine endpoint could not be inspected.'
    }
    # Never trust a TCP/SSH context: it might execute on a remote machine.
    $endpoint = $endpoint.Trim('"')
    if (-not $endpoint.StartsWith('npipe://', [StringComparison]::OrdinalIgnoreCase)) {
        Block 'Docker context is not a local Windows named-pipe endpoint.'
    }
    $raw = (& docker info --format '{{json .}}' 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) {
        Block 'Docker Desktop Linux engine is not available.'
    }
    $info = $raw | ConvertFrom-Json
    if ($info.OSType -cne 'linux') {
        Block 'Docker daemon is not in Linux container mode.'
    }
    if ($info.OperatingSystem -notmatch 'Docker Desktop') {
        Block 'Connected daemon did not identify itself as Docker Desktop.'
    }
    if ($info.PidsLimit -eq $false -or $info.MemoryLimit -eq $false) {
        Block 'Docker daemon did not advertise required PID/memory constraints.'
    }
    Write-Host 'WINDOWS_STATIC_WEB_SANDBOX: LOCAL_DOCKER_METADATA_READY'
    Write-Host ('  context: ' + $context)
    Write-Host '  engine: Docker Desktop (Linux containers)'
    Write-Host '  runtime profile: UNQUALIFIED'
    Write-Host '  no image or Chromium guest has been executed by this inspection'
    Write-Host '  qualified: false; executionEnabled: false'
    exit 0
}
catch {
    Block 'Local Docker Desktop metadata inspection failed.'
}
