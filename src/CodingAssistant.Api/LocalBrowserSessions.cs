using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CodingAssistant.Api;

/// <summary>
/// Stateless 12-hour browser bearer for the same-origin Windows UI.
/// The token embeds its issue time, random nonce and a truncated HMAC bound
/// to the private Windows owner key. No persistent owner key is returned,
/// no cookies are used, and refreshing the page needs no manual login.
/// This never grants authority to run generated code.
/// </summary>
public sealed class LocalBrowserSessions(byte[] ownerKey)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    public string Issue()
    {
        var bytes = new byte[32];
        var now = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), now);
        RandomNumberGenerator.Fill(bytes.AsSpan(4, 12));
        var mac = HMACSHA256.HashData(ownerKey, bytes.AsSpan(0, 16));
        mac.AsSpan(0, 16).CopyTo(bytes.AsSpan(16, 16));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public bool IsValid(string token)
    {
        if (token.Length != 64 ||
            token.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return false;
        byte[] bytes;
        try { bytes = Convert.FromHexString(token); }
        catch (FormatException) { return false; }
        var issued = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (issued > now || now - issued > Lifetime.TotalSeconds)
            return false;
        var expectedMac = HMACSHA256.HashData(ownerKey, bytes.AsSpan(0, 16));
        return CryptographicOperations.FixedTimeEquals(
            bytes.AsSpan(16, 16), expectedMac.AsSpan(0, 16));
    }

    /// <summary>
    /// Strict browser-only, same-origin bootstrap. No CORS is enabled.
    /// Cross-origin websites cannot set the custom header in a simple
    /// request, and browsers cannot forge Sec-Fetch-Site. The Host check
    /// prevents DNS rebinding into the local loopback listener.
    /// </summary>
    public static bool MayBootstrap(HttpContext context)
    {
        var request = context.Request;
        return OperatingSystem.IsWindows() &&
            HttpMethods.IsPost(request.Method) &&
            string.Equals(request.Host.Host, "127.0.0.1",
                StringComparison.Ordinal) &&
            string.Equals(request.Headers["Sec-Fetch-Site"],
                "same-origin", StringComparison.Ordinal) &&
            string.Equals(request.Headers["X-CodingAssistant-Client"],
                "mission-control", StringComparison.Ordinal) &&
            string.Equals(request.Headers.Origin,
                $"http://127.0.0.1:{request.Host.Port}",
                StringComparison.Ordinal);
    }
}
