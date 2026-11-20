# Windows PowerShell 5.1 / .NET 10 DPAPI CurrentUser shared credential.
# Do not echo owner tokens, put them in source, URLs or process arguments.
function New-CodingAssistantOwnerToken {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    }
    finally {
        $rng.Dispose()
    }
    return ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
}

function Get-CodingAssistantOwnerToken {
    param([string]$PrivateDataDirectory)

    if ([string]::IsNullOrWhiteSpace($PrivateDataDirectory)) {
        $PrivateDataDirectory = $env:CODINGASSISTANT_DATA_DIR
    }
    if ([string]::IsNullOrWhiteSpace($PrivateDataDirectory)) {
        $PrivateDataDirectory = Join-Path (
            [Environment]::GetFolderPath(
                [Environment+SpecialFolder]::LocalApplicationData)) 'CodingAssistant'
    }
    $PrivateDataDirectory = [IO.Path]::GetFullPath($PrivateDataDirectory)
    if (-not [IO.Directory]::Exists($PrivateDataDirectory)) {
        [IO.Directory]::CreateDirectory($PrivateDataDirectory) | Out-Null
    }
    $directory = Get-Item -LiteralPath $PrivateDataDirectory -Force
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Refusing redirected owner-token directory.'
    }

    $path = Join-Path $PrivateDataDirectory 'owner-token.dpapi'
    $entropy = [Text.Encoding]::UTF8.GetBytes('CodingAssistant|owner-token|v1')
    if (-not [IO.File]::Exists($path)) {
        $token = New-CodingAssistantOwnerToken
        $encrypted = [Security.Cryptography.ProtectedData]::Protect(
            [Text.Encoding]::ASCII.GetBytes($token), $entropy,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $temporary = Join-Path $PrivateDataDirectory (
            'owner-token-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        try {
            [IO.File]::WriteAllText(
                $temporary, [Convert]::ToBase64String($encrypted),
                [Text.Encoding]::ASCII)
            try {
                # Move fails if another VS/launcher process already created
                # its credential; read the established winner instead.
                [IO.File]::Move($temporary, $path)
            }
            catch [IO.IOException] {
                if (-not [IO.File]::Exists($path)) { throw }
            }
        }
        finally {
            if ([IO.File]::Exists($temporary)) {
                [IO.File]::Delete($temporary)
            }
            [Array]::Clear($encrypted, 0, $encrypted.Length)
        }
    }

    $file = Get-Item -LiteralPath $path -Force
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Refusing redirected owner-token file.'
    }
    if ($file.Length -lt 20 -or $file.Length -gt 2048) {
        throw 'The saved owner credential is invalid; it was not replaced.'
    }
    try {
        $payload = [IO.File]::ReadAllText($path, [Text.Encoding]::ASCII).Trim()
        $ciphertext = [Convert]::FromBase64String($payload)
        $cleartext = [Security.Cryptography.ProtectedData]::Unprotect(
            $ciphertext, $entropy,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
        try {
            $value = [Text.Encoding]::ASCII.GetString($cleartext)
            if ($value -cnotmatch '^[0-9a-f]{64}$') {
                throw 'Saved owner credential format is invalid.'
            }
            return $value
        }
        finally {
            [Array]::Clear($cleartext, 0, $cleartext.Length)
            [Array]::Clear($ciphertext, 0, $ciphertext.Length)
        }
    }
    catch {
        throw 'Cannot decrypt the saved owner credential for this Windows account. The credential was not replaced.'
    }
}
