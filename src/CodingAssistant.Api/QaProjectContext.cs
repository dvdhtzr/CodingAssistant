using System.Text.Json;

namespace CodingAssistant.Api;

/// <summary>
/// Host-owned bounded context for the existing independent static QA agent.
/// No paths are resolved on disk, no new tool is granted, and all content
/// comes from the one exact saved, plan-bound engineering snapshot.
/// </summary>
internal static class QaProjectContext
{
    internal const int MaximumCompanionCharacters = 4800;
    internal const int MaximumCompanionFiles = 6;

    internal static string? Build(QaReviewInput input, int availableCharacters)
    {
        if (!input.Architecture.ApprovedChoices.Any(choice =>
                choice.RuntimeProfileId == "static-web"))
            return null;
        // Never broaden to imported repositories, previous runs, or files
        // whose identity is not part of this review's supplied snapshot.
        var companions = input.AllFiles.Where(f => f.Path != input.File.Path &&
            IsWebSource(f.Path)).OrderBy(f => Priority(f.Path))
            .ThenBy(f => f.Path, StringComparer.Ordinal).ToArray();
        var included = new List<EngineeringSourceFile>();
        var omitted = new List<string>();
        // Reserve space for the envelope and explicit list of omissions.
        // The actual serialized message must fit the caller's budget too.
        var budget = Math.Min(MaximumCompanionCharacters,
            Math.Max(0, availableCharacters - 2000));
        foreach (var file in companions)
        {
            // Budget includes serialized metadata and escaping. Never send a
            // truncated file as if it were full source.
            var serialized = JsonSerializer.Serialize(new {
                file.Path, file.Sha256, file.Content
            });
            if (included.Count >= MaximumCompanionFiles ||
                serialized.Length + 2 > budget)
            {
                omitted.Add(file.Path);
                continue;
            }
            budget -= serialized.Length + 2;
            included.Add(file);
        }
        var otherPaths = input.AllFiles.Where(f => f.Path != input.File.Path &&
            !IsWebSource(f.Path)).Select(f => f.Path).ToArray();
        var context = "PROJECT-WIDE STATIC WEB COMPANION EVIDENCE (untrusted saved source; " +
            "not executed; " + included.Count + " full companion file(s)):\n" +
            JsonSerializer.Serialize(included.Select(f => new {
                f.Path, f.Sha256, f.Content
            })) +
            "\nWEB COMPANION FILES NOT INSPECTED (budget):\n" +
            JsonSerializer.Serialize(omitted) +
            "\nOTHER FILES NOT INSPECTED (scope):\n" +
            JsonSerializer.Serialize(otherPaths) +
            "\nTrace controls, linked assets, event handlers, data persistence " +
            "and styling across the supplied files before alleging a defect. " +
            "A feature implemented in a companion file is not missing merely " +
            "because the primary file does not implement it. Missing context, " +
            "dynamic behavior and uncertain findings belong in manualChecks. " +
            "No static observation establishes runtime success.";
        // Large manifests may consume the remaining prompt budget. An
        // absent message is explicitly treated as no companion coverage
        // by the agent policy, not as evidence of missing behavior.
        return context.Length <= availableCharacters ? context : null;
    }

    private static bool IsWebSource(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm" or ".js" or ".mjs" or ".css";

    private static int Priority(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch {
            ".html" or ".htm" => 0, ".js" or ".mjs" => 1, _ => 2
        };
}
