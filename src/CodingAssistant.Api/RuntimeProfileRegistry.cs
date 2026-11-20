namespace CodingAssistant.Api;

/// <summary>
/// Planning-only descriptions. They are never executable admissions.
/// Existing M1 execution is not dispatched through this registry.
/// </summary>
public sealed record RuntimeResourceCeilings(
    int MaxMemoryMib, int MaxCpuSeconds, int MaxProcesses, int MaxWallSeconds);

public sealed record RuntimeProfileDescriptor(
    int SchemaVersion,
    string Id,
    string Version,
    string Description,
    string QualificationStatus,
    bool ExecutableViaRegistry,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> SourceExtensions,
    IReadOnlyList<string> HostPlatforms,
    string? ToolchainImageDigest,
    string? EvaluatorImageDigest,
    string DependencyPolicy,
    IReadOnlyList<string> RequiredIndependentTests,
    RuntimeResourceCeilings? ResourceCeilings,
    string SecurityPolicy,
    string QualificationEvidence);

public static class RuntimeProfileRegistry
{
    public const int SchemaVersion = 1;
    private static IReadOnlyList<string> ReadOnly(params string[] items) =>
        Array.AsReadOnly(items);

    private static readonly IReadOnlyList<RuntimeProfileDescriptor> Entries =
        Array.AsReadOnly(new[]
        {
            new RuntimeProfileDescriptor(
                SchemaVersion, "static-web", "0.1.0",
                "Planned offline HTML/CSS/JavaScript + independent Chromium verification",
                "UNQUALIFIED", false,
                ReadOnly("HTML", "CSS", "JavaScript"),
                ReadOnly(".html", ".css", ".js", ".json"),
                ReadOnly("Linux (planned)", "Windows (separate future qualification)"),
                null, null, "No automatic package downloads",
                ReadOnly("Linked asset integrity", "JavaScript syntax",
                    "Chromium mouse and keyboard behavior", "History persistence",
                    "Adversarial isolation and cleanup"),
                null, "No executor, guest, shell or network access is implemented",
                "Not qualified; Calculation Journal is a negative acceptance fixture"),
            new RuntimeProfileDescriptor(
                SchemaVersion, "legacy-dotnet-pilot", "0.1.0",
                "Existing narrow, pinned C#/.csproj task coding pilot; NOT general .NET support",
                "LEGACY_SCOPED_PILOT_ONLY", false,
                ReadOnly("C#"), ReadOnly(".cs", ".csproj"),
                ReadOnly("Qualified Linux pilot only; Windows executable admission disabled"),
                null, null, "Existing pinned, offline M1 pilot policy only",
                ReadOnly("Existing M1 scoped qualification; not general project verification"),
                null, "No new permissions; existing M1 exact-scope checks remain authoritative",
                "docs/M1_TASK_PROJECT_PILOT.md and docs/SECURITY.md")
        });

    public static IReadOnlyList<RuntimeProfileDescriptor> List() => Entries;

    public static RuntimeProfileDescriptor? Find(string? id) =>
        Entries.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
}
