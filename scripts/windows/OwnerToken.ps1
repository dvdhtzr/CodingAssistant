# Token generation shared by the Windows launcher and model-free test.
# Windows PowerShell 5.1 does not support [Convert]::ToHexString().
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
