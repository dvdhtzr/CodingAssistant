using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

public sealed record StaticWebPreflightFinding(
    string Code, string Severity, string Path, string Detail,
    string? SourceExcerpt = null, string? ReferencedPath = null);
public sealed record StaticWebPreflightReport(
    string RunId, string SourceState, string? SourceSha256,
    string Status, string VerificationStatus,
    int HtmlFileCount, int ReferencedAssets,
    IReadOnlyList<StaticWebPreflightFinding> Findings);

/// <summary>
/// Pure, bounded inspection of persisted source text. This never writes files,
/// parses/runs JavaScript with a runtime, invokes a model or opens a browser.
/// It is a helpful preflight, NOT independent build/test or VERIFIED evidence.
/// </summary>
public static class StaticWebSourcePreflight
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex Tag = new(
        @"<(?<tag>[a-z][a-z0-9:-]*)\b(?<attrs>[^<>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Attribute = new(
        @"(?<key>[a-z_:][a-z0-9_:\-]*)\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<bare>[^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Selector = new(
        @"\b(?<method>querySelectorAll|querySelector|getElementById)\s*\(\s*['""](?<value>[^'""]+)['""]",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex SimpleSelector = new(
        @"^[#.][A-Za-z_][A-Za-z0-9_-]*$",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex ScriptBody = new(
        @"<script\b(?<attrs>[^<>]*)>(?<body>.*?)</script\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex ScriptComment = new(
        @"/\*.*?\*/|//[^\r\n]*",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexTimeout);

    private static Dictionary<string, string> Attributes(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in Attribute.Matches(text))
        {
            var value = attribute.Groups["dq"].Success ? attribute.Groups["dq"].Value :
                attribute.Groups["sq"].Success ? attribute.Groups["sq"].Value :
                attribute.Groups["bare"].Value;
            values.TryAdd(attribute.Groups["key"].Value, value);
        }
        return values;
    }

    private static string? LocalPath(string owner, string reference)
    {
        reference = reference.Trim();
        if (reference.Length == 0 || reference.StartsWith("//", StringComparison.Ordinal) ||
            reference.StartsWith('#') || reference.Contains('\\') ||
            Regex.IsMatch(reference, @"^[a-z][a-z0-9+.-]*:",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
            return null;
        var clean = reference.Split(['?', '#'], 2)[0];
        if (clean.Length == 0) return null;
        var absolute = clean.StartsWith('/');
        var segments = new List<string>();
        if (!absolute)
        {
            var slash = owner.LastIndexOf('/');
            if (slash >= 0) segments.AddRange(owner[..slash].Split('/'));
        }
        foreach (var part in clean.TrimStart('/').Split('/'))
        {
            if (part == "" || part == ".") continue;
            if (part == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(part);
            if (segments.Count > 5) return null;
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    public static StaticWebPreflightReport Inspect(EngineeringRunSnapshot snapshot)
    {
        var job = snapshot.Job;
        var files = snapshot.Files;
        if (job.State == "Running")
            return new(job.Id, job.State, null, "PENDING_SOURCE",
                "GENERATED_NOT_TESTED", 0, 0, []);
        if (files.Count == 0)
            return new(job.Id, job.State, null, "NO_SAVED_SOURCE",
                "GENERATED_NOT_TESTED", 0, 0, []);
        if (files.Count > EngineeringRunStore.MaxDistinctFiles ||
            files.Sum(f => (long)f.Content.Length) > EngineeringRunStore.MaxTotalCharacters)
            return new(job.Id, job.State, null, "BOUNDS_EXCEEDED",
                "GENERATED_NOT_TESTED", 0, 0, []);

        var fingerprint = QaReviewStore.Fingerprint(files);
        var html = files.Where(f => f.Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (html.Length == 0)
            return new(job.Id, job.State, fingerprint, "NOT_STATIC_WEB",
                "GENERATED_NOT_TESTED", 0, 0, []);

        var findings = new List<StaticWebPreflightFinding>();
        var paths = files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var classes = new HashSet<string>(StringComparer.Ordinal);
        var linkedScripts = 0;
        var references = 0;
        void Finding(string code, string severity, string path, string detail,
            string? excerpt = null, string? referencedPath = null)
        {
            if (findings.Count < 60)
                findings.Add(new(code, severity, path,
                    detail[..Math.Min(500, detail.Length)],
                    excerpt is { Length: <= 240 } ? excerpt : null,
                    referencedPath is { Length: <= 240 } ? referencedPath : null));
        }

        foreach (var file in html)
        {
            foreach (Match tag in Tag.Matches(file.Content))
            {
                var attributes = Attributes(tag.Groups["attrs"].Value);
                if (attributes.TryGetValue("id", out var id)) ids.Add(id);
                if (attributes.TryGetValue("class", out var cssClass))
                    foreach (var cls in cssClass.Split((char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries))
                        classes.Add(cls);
                var element = tag.Groups["tag"].Value;
                var isScript = element.Equals("script", StringComparison.OrdinalIgnoreCase);
                var isCss = element.Equals("link", StringComparison.OrdinalIgnoreCase) &&
                    attributes.TryGetValue("rel", out var rel) &&
                    rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                        .Contains("stylesheet", StringComparer.OrdinalIgnoreCase);
                if (!isScript && !isCss) continue;
                var hasRef = attributes.TryGetValue(isScript ? "src" : "href",
                    out var reference);
                if (!hasRef) continue;
                references++;
                var requested = reference ?? string.Empty;
                var localPath = LocalPath(file.Path, requested);
                if (localPath is null)
                {
                    Finding("UNRESOLVED_ASSET_REFERENCE", "Error", file.Path,
                        "Asset reference is external, invalid, or outside the project: " +
                        requested[..Math.Min(requested.Length, 140)], tag.Value);
                    continue;
                }
                if (!paths.ContainsKey(localPath))
                {
                    Finding("MISSING_LINKED_ASSET", "Error", file.Path,
                        "Referenced asset is not saved: " + localPath, tag.Value, localPath);
                    continue;
                }
                if (isScript && localPath.EndsWith(".js",
                    StringComparison.OrdinalIgnoreCase)) linkedScripts++;
            }
            foreach (Match script in ScriptBody.Matches(file.Content))
            {
                if (Attributes(script.Groups["attrs"].Value).ContainsKey("src")) continue;
                var body = script.Groups["body"].Value;
                if (body.Trim().Length > 0 &&
                    ScriptComment.Replace(body, "").Trim().Length == 0)
                    Finding("COMMENT_ONLY_INLINE_SCRIPT", "Warning", file.Path,
                        "Inline script contains only comments; no executable behavior is evident.", script.Value);
            }
        }
        if (linkedScripts == 0 && files.Any(f =>
            f.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)))
            Finding("NO_LINKED_JAVASCRIPT", "Warning", html[0].Path,
                "Saved JavaScript exists, but no local .js script is linked by the HTML.");

        foreach (var js in files.Where(f =>
            f.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (Match reference in Selector.Matches(js.Content))
            {
                var method = reference.Groups["method"].Value;
                var value = reference.Groups["value"].Value;
                var kind = method == "getElementById" ? "#" :
                    SimpleSelector.IsMatch(value) ? value[..1] : null;
                if (kind is null) continue; // Complex/dynamic selectors require a browser.
                var name = method == "getElementById" ? value : value[1..];
                if (name.Length > 100) continue;
                var exists = kind == "#" ? ids.Contains(name) : classes.Contains(name);
                if (!exists)
                    Finding("SELECTOR_NOT_DECLARED", "Warning", js.Path,
                        "Literal selector was not declared in saved HTML: " +
                        (kind == "#" ? "#" : ".") + name, reference.Value);
            }
        }
        return new(job.Id, job.State, fingerprint,
            findings.Count == 0 ? "NO_ISSUES_DETECTED" : "POTENTIAL_ISSUES",
            "GENERATED_NOT_TESTED", html.Length, references, findings.AsReadOnly());
    }
}
