using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

public sealed record ProjectSourceFinding(
    string Code, string Severity, string Path, string Detail);

public sealed record ProjectSourceReadinessReport(
    string RunId, string SourceState, string? SourceSha256,
    string ProjectKind, string? RuntimeProfileId,
    string Status, string VerificationStatus, bool ExecutionAvailable,
    IReadOnlyList<ProjectSourceFinding> Findings);

/// <summary>
/// Bounded, model-free inspection of a saved engineering source snapshot.
/// Reports missing project structure and possible runtime requirements.
/// It never evaluates source, runs package managers or qualifies execution.
/// </summary>
public static class ProjectSourceReadiness
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
    private static readonly Regex ServerCode = new(
        @"\b(?:require\s*\(\s*['""](?:express|node:http|http|https|fastify|koa)['""]\s*\)|from\s*['""](?:express|node:http|http|fastify|koa)['""]|(?:app|server)\s*\.\s*listen\s*\(|createServer\s*\()",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Timeout);
    private static readonly Regex ModuleImport = new(
        @"(?:\bfrom\s*|\brequire\s*\(\s*|\bimport\s*\(\s*|\bimport\s*)['""](?<name>[^'""]{1,120})['""]",
        RegexOptions.CultureInvariant, Timeout);
    private static readonly HashSet<string> NodeBuiltins = new(
        ["assert", "buffer", "child_process", "crypto", "dns", "events", "fs",
         "http", "http2", "https", "module", "net", "os", "path", "process",
         "querystring", "readline", "stream", "timers", "tls", "url",
         "util", "vm", "worker_threads", "zlib", "test"],
        StringComparer.Ordinal);

    private static bool IsScript(string path) =>
        path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);

    private static bool IsTest(string path) =>
        path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(".test.", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(".spec.", StringComparison.OrdinalIgnoreCase);

    private static string? PackageName(string imported)
    {
        if (imported.StartsWith("node:", StringComparison.Ordinal) ||
            imported.StartsWith('.') || imported.StartsWith('/') ||
            NodeBuiltins.Contains(imported))
            return null;
        if (imported.StartsWith('@'))
        {
            var parts = imported.Split('/');
            return parts.Length >= 2 ? parts[0] + "/" + parts[1] : null;
        }
        return imported.Split('/')[0];
    }

    private static bool HasNonEmptyProperty(JsonElement parent, string name)
    {
        return parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString());
    }

    public static ProjectSourceReadinessReport Inspect(EngineeringRunSnapshot snapshot)
    {
        var run = snapshot.Job;
        if (run.State == "Running")
            return new(run.Id, run.State, null, "UNKNOWN", null,
                "PENDING_SOURCE", "GENERATED_NOT_TESTED", false, []);
        if (snapshot.Files.Count == 0)
            return new(run.Id, run.State, null, "UNKNOWN", null,
                "NO_SAVED_SOURCE", "GENERATED_NOT_TESTED", false, []);

        var findings = new List<ProjectSourceFinding>();
        void Add(string code, string severity, string path, string detail)
        {
            if (findings.Count < 30)
                findings.Add(new(code, severity, path, detail));
        }

        if (snapshot.Files.Count > EngineeringRunStore.MaxDistinctFiles ||
            snapshot.Files.Sum(f => (long)f.Content.Length) >
                EngineeringRunStore.MaxTotalCharacters)
            return new(run.Id, run.State, null, "UNKNOWN", null,
                "SOURCE_BOUNDS_EXCEEDED", "GENERATED_NOT_TESTED", false,
                [new("SOURCE_BOUNDS_EXCEEDED", "Error", "",
                    "Saved source exceeds bounded inspection limits.")]);

        var files = snapshot.Files.ToDictionary(
            f => f.Path, StringComparer.OrdinalIgnoreCase);
        var scripts = snapshot.Files.Where(f => IsScript(f.Path)).ToArray();
        var server = scripts.Any(f => ServerCode.IsMatch(f.Content));
        var hasHtml = snapshot.Files.Any(f =>
            f.Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
        var hasCSharp = snapshot.Files.Any(f =>
            f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        var hasProject = snapshot.Files.Any(f =>
            f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        var kind = server ? "NODE_SERVER" : hasCSharp || hasProject
            ? "DOTNET_PROJECT" : hasHtml ? "STATIC_WEB" : "UNKNOWN";
        var profile = kind switch
        {
            "NODE_SERVER" => "node-server",
            "STATIC_WEB" => "static-web",
            _ => null
        };

        if (kind == "NODE_SERVER")
        {
            if (!files.TryGetValue("package.json", out var package))
            {
                Add("MISSING_PACKAGE_MANIFEST", "Error", "package.json",
                    "This Node.js server imports packages but has no package.json.");
            }
            else
            {
                try
                {
                    using var document = JsonDocument.Parse(package.Content);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        throw new JsonException("Package manifest must be an object");
                    var scriptsValue = root.TryGetProperty("scripts", out var s)
                        ? s : default;
                    if (!HasNonEmptyProperty(scriptsValue, "start"))
                        Add("MISSING_START_SCRIPT", "Error", "package.json",
                            "Declare a reproducible start script for the server.");
                    if (!HasNonEmptyProperty(scriptsValue, "test") ||
                        scriptsValue.GetProperty("test").GetString()!
                            .Contains("no test specified", StringComparison.OrdinalIgnoreCase))
                        Add("MISSING_TEST_SCRIPT", "Error", "package.json",
                            "Declare a real test command, not the npm placeholder.");
                    var declared = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var section in new[] { "dependencies", "devDependencies" })
                        if (root.TryGetProperty(section, out var value) &&
                            value.ValueKind == JsonValueKind.Object)
                            foreach (var item in value.EnumerateObject())
                                declared.Add(item.Name);

                    foreach (var imported in scripts
                        .SelectMany(f => ModuleImport.Matches(f.Content)
                            .Cast<Match>().Select(m => m.Groups["name"].Value))
                        .Select(PackageName).Where(p => p is not null)
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    {
                        if (!declared.Contains(imported!))
                            Add("UNDECLARED_DEPENDENCY", "Error", "package.json",
                                "Imported package is missing from dependencies: " + imported);
                    }
                }
                catch (JsonException)
                {
                    Add("INVALID_PACKAGE_MANIFEST", "Error", "package.json",
                        "package.json must be a valid JSON object.");
                }
            }
            if (!snapshot.Files.Any(f => IsScript(f.Path) && IsTest(f.Path)))
                Add("MISSING_TEST_FILES", "Error", "tests/",
                    "No Node.js test source was saved.");
            if (!files.ContainsKey("README.md"))
                Add("MISSING_STARTUP_INSTRUCTIONS", "Warning", "README.md",
                    "Include instructions for installation, configuration and startup.");
        }
        else if (kind == "STATIC_WEB")
        {
            if (!files.ContainsKey("index.html"))
                Add("MISSING_ROOT_INDEX_HTML", "Error", "index.html",
                    "A static website needs a root index.html.");
            var web = StaticWebSourcePreflight.Inspect(snapshot);
            foreach (var finding in web.Findings)
                Add(finding.Code, finding.Severity, finding.Path, finding.Detail);
        }
        else if (kind == "DOTNET_PROJECT")
        {
            if (!hasProject)
                Add("MISSING_DOTNET_PROJECT", "Error", "",
                    "C# source needs a project file before a build can be attempted.");
        }
        else
        {
            Add("UNKNOWN_PROJECT_STRUCTURE", "Warning", "",
                "The saved files do not establish a supported project type.");
        }

        var status = run.State != "Generated" ? "PARTIAL_SOURCE" :
            findings.Any(f => f.Severity == "Error") ? "INCOMPLETE" :
            findings.Count > 0 ? "REVIEW_REQUIRED" : "SOURCE_READY_FOR_REVIEW";
        return new(run.Id, run.State, QaReviewStore.Fingerprint(snapshot.Files),
            kind, profile, status, "GENERATED_NOT_TESTED", false,
            findings.AsReadOnly());
    }
}
