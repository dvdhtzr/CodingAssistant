using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

/// <summary>
/// Bounded, in-memory, owner-private request *metadata*. Not an audit ledger.
/// No request URL, query string, route value, body, headers, token, model output,
/// generated source or exception text is collected. All records are ephemeral.
/// </summary>
public sealed record LocalHttpActivity(
    long Sequence, string RecordedUtc, string Method,
    string Route, int StatusCode, int DurationMs);

public sealed record LocalHttpActivityPage(
    IReadOnlyList<LocalHttpActivity> Events, long LastSequence,
    bool Truncated, int Capacity);

public sealed class LocalHttpActivityLedger
{
    public const int MaxEntries = 120;
    private const int MaxPage = 40;
    private static readonly Regex SafeTemplate = new(
        "^[a-zA-Z0-9_./{}:?-]{1,160}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private readonly Queue<LocalHttpActivity> _recent = new();
    private long _lastSequence;

    public void Record(string method, string? routeTemplate, int status, double elapsedMs)
    {
        // The route must originate from the server-defined endpoint pattern,
        // never the request path. Fail closed for unknown endpoint templates.
        var route = routeTemplate is not null && SafeTemplate.IsMatch(routeTemplate)
            ? routeTemplate : "API request";
        var safeMethod = method is "GET" or "POST" or "DELETE" or "PUT" or "PATCH"
            ? method : "OTHER";
        var safeStatus = status is >= 100 and <= 599 ? status : 500;
        var ms = double.IsFinite(elapsedMs)
            ? (int)Math.Clamp(Math.Round(elapsedMs), 0, 600_000)
            : 0;
        lock (_gate)
        {
            var item = new LocalHttpActivity(++_lastSequence,
                DateTimeOffset.UtcNow.ToString("O"), safeMethod, route, safeStatus, ms);
            _recent.Enqueue(item);
            if (_recent.Count > MaxEntries) _recent.Dequeue();
        }
    }

    public LocalHttpActivityPage Read(long after = 0, int limit = 20)
    {
        var boundedAfter = Math.Max(0, after);
        var boundedLimit = Math.Clamp(limit, 1, MaxPage);
        lock (_gate)
        {
            var oldest = _recent.Count > 0 ? _recent.Peek().Sequence : _lastSequence + 1;
            var truncated = boundedAfter > 0 && boundedAfter < oldest - 1;
            var items = _recent.Where(e => e.Sequence > boundedAfter).ToArray();
            if (items.Length > boundedLimit)
            {
                truncated = truncated || boundedAfter > 0;
                items = items[^boundedLimit..];
            }
            return new LocalHttpActivityPage(items, _lastSequence,
                truncated, MaxEntries);
        }
    }
}
