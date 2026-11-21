using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CodingAssistant.Api;

/// <summary>
/// Short-lived, process-local browser session for same-origin Windows Mission
/// Control. Never exposes the long-lived DPAPI owner token to JavaScript.
/// A session is an additional bearer, NOT executable authorization.
/// </summary>
public sealed class LocalBrowserSessions
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    private const int MaxActiveSessions = 32;

    public string Issue()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _sessions)
            if (entry.Value <= now) _sessions.TryRemove(entry.Key, out _);
        if (_sessions.Count >= MaxActiveSessions)
            throw new StoreFailure("LOCAL_SESSION_LIMIT");

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            .ToLowerInvariant();
        var hashed = Hash(token);
        _sessions[hashed] = now.Add(Lifetime);
        return token;
    }

    public bool IsValid(string token)
    {
        if (token.Length != 64 || !token.All(c =>
                c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return false;
        var digest = Hash(token);
        if (!_sessions.TryGetValue(digest, out var expiration))
            return false;
        if (expiration > DateTimeOffset.UtcNow)
            return true;
        _sessions.TryRemove(digest, out _);
        return false;
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    /// <summary>
    /// The bootstrap is deliberately browser-only and same-origin. A cross-site
    /// page cannot send our non-simple header without a CORS preflight (which
    /// is not enabled) and cannot forge Sec-Fetch-Site from browser JS.
    /// Host checking prevents DNS rebinding into a loopback listener.
    /// </summary>
    public static bool MayBootstrap(HttpContext context)
    {
        var request = context.Request;
        if (!OperatingSystem.IsWindows() ||
            !HttpMethods.IsPost(request.Method) ||
            !string.Equals(request.Host.Host, "127.0.0.1",
                StringComparison.Ordinal) ||
            !string.Equals(request.Headers["Sec-Fetch-Site"],
                "same-origin", StringComparison.Ordinal) ||
            !string.Equals(request.Headers["X-CodingAssistant-Client"],
                "mission-control", StringComparison.Ordinal) ||
            !string.Equals(request.Headers.Origin,
                $"http://127.0.0.1:{request.Host.Port}",
                StringComparison.Ordinal))
            return false;
        return true;
    }
}
