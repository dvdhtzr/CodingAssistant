using System.Text.Json;

namespace CodingAssistant.Api;

public sealed record AtlasInspectionRequest(string Name, string Argument);
public sealed record AtlasInspectionObservation(
    string Name, string SourceRevision, int ResultCount);
public sealed record AtlasInspectionResult(
    string Name, string SourceRevision, string Coverage, int ResultCount,
    IReadOnlyList<AtlasAgentSymbol> Symbols,
    IReadOnlyList<AtlasAgentRelationship> Relationships,
    AtlasAgentSourceEvidence? SourceEvidence);

/// <summary>
/// Host-brokered, read-only, model-requested Code Atlas observations.
/// This is NOT an arbitrary MAF/Ollama function-call capability: the local
/// adapter still prohibits ChatOptions.Tools. The model may select only
/// these literal operations via a validated JSON request. No filesystem,
/// process, execution, installation, or network capability is introduced.
/// </summary>
public static class AgentAtlasInspectionGateway
{
    public const int MaximumRequestsPerTask = 2;

    public static bool TryParseRequest(string? text,
        out AtlasInspectionRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(text) ||
            !text.Contains("\"toolRequest\"", StringComparison.Ordinal))
            return false;
        if (text.Length > 1000) throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("toolRequest", out var tool) ||
                tool.ValueKind != JsonValueKind.Object ||
                tool.EnumerateObject().Count() != 2 ||
                !tool.TryGetProperty("name", out var name) ||
                !tool.TryGetProperty("argument", out var argument) ||
                name.ValueKind != JsonValueKind.String ||
                argument.ValueKind != JsonValueKind.String)
                throw new StoreFailure("MODEL_OUTPUT_REJECTED");
            var operation = name.GetString();
            var value = argument.GetString();
            if (operation is not ("atlas.find_symbols" or
                "atlas.get_relationships" or "atlas.source_excerpt") ||
                string.IsNullOrWhiteSpace(value) || value.Length > 80 ||
                value.Any(char.IsControl))
                throw new StoreFailure("MODEL_OUTPUT_REJECTED");
            request = new AtlasInspectionRequest(operation, value);
            return true;
        }
        catch (JsonException)
        {
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        }
    }

    public static AtlasInspectionResult Execute(string planSha256,
        IReadOnlyList<EngineeringSourceFile> priorFiles,
        AtlasInspectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(priorFiles);
        ArgumentNullException.ThrowIfNull(request);
        if (planSha256 is null || planSha256.Length != 64 ||
            !planSha256.All(Uri.IsHexDigit) ||
            priorFiles.Count is < 1 or > EngineeringRunStore.MaxDistinctFiles ||
            priorFiles.Any(file => file.Content.Length >
                ProjectDraftStore.MaxFileCharacters) ||
            request.Name is not ("atlas.find_symbols" or
                "atlas.get_relationships" or "atlas.source_excerpt") ||
            string.IsNullOrWhiteSpace(request.Argument) ||
            request.Argument.Length > 80 ||
            request.Argument.Any(char.IsControl))
            throw new StoreFailure("INVALID_INPUT");

        // Graph input is ONLY the already-persisted files admitted to this
        // exact engineering task. No imported repository, prior run or other
        // project can be queried. This indexer never reads paths from disk.
        var graph = CodeAtlasIndexer.Index(
            "engineer-inspection-" + planSha256[..12], priorFiles);
        var allowedPaths = priorFiles.Select(f => f.Path)
            .ToHashSet(StringComparer.Ordinal);
        var nodes = graph.Nodes.Where(node =>
            node.Path is not null && allowedPaths.Contains(node.Path))
            .ToDictionary(node => node.Id, StringComparer.Ordinal);
        var symbols = new List<AtlasAgentSymbol>();
        var relationships = new List<AtlasAgentRelationship>();
        AtlasAgentSourceEvidence? evidence = null;

        if (request.Name == "atlas.find_symbols")
        {
            symbols.AddRange(nodes.Values.Where(node =>
                node.Kind is "Class" or "Interface" or "Struct" or
                    "Enum" or "Method" or "Constructor" or "Function" or
                    "File" &&
                (node.Name.Contains(request.Argument,
                    StringComparison.OrdinalIgnoreCase) ||
                 node.QualifiedName.Contains(request.Argument,
                    StringComparison.OrdinalIgnoreCase)))
                .OrderBy(node => node.Path, StringComparer.Ordinal)
                .ThenBy(node => node.Line)
                .ThenBy(node => node.Id, StringComparer.Ordinal)
                .Take(12)
                .Select(node => new AtlasAgentSymbol(node.Id, node.Kind,
                    node.Name, node.Path!, node.Line)));
        }
        else if (nodes.TryGetValue(request.Argument, out var target))
        {
            if (request.Name == "atlas.get_relationships")
            {
                relationships.AddRange(graph.Edges
                    .Where(edge => edge.SourceId == target.Id ||
                                   edge.TargetId == target.Id)
                    .Select(edge =>
                    {
                        var otherId = edge.SourceId == target.Id ?
                            edge.TargetId : edge.SourceId;
                        return nodes.TryGetValue(otherId, out var other)
                            ? new AtlasAgentRelationship(
                                edge.SourceId == target.Id ? "Outgoing" :
                                    "Incoming",
                                edge.Kind, otherId, other.Name,
                                edge.EvidencePath, edge.EvidenceLine,
                                edge.Resolution)
                            : null;
                    })
                    .Where(edge => edge is not null)
                    .Take(16).Select(edge => edge!));
            }
            else if (request.Name == "atlas.source_excerpt" &&
                target.Line is > 0)
            {
                var source = priorFiles.Single(file =>
                    file.Path == target.Path);
                var lines = source.Content.Split('\n');
                var start = Math.Max(0, target.Line.Value - 2);
                var excerpt = string.Join("\n", lines.Skip(start).Take(6)
                    .Select((line, index) => (start + index + 1) +
                        ": " + line.TrimEnd('\r')));
                if (excerpt.Length > 1000) excerpt = excerpt[..1000];
                evidence = new AtlasAgentSourceEvidence(
                    target.Id, source.Path, start + 1, excerpt);
            }
        }
        return new AtlasInspectionResult(request.Name,
            graph.SourceRevision, graph.Coverage,
            symbols.Count + relationships.Count +
                (evidence is null ? 0 : 1),
            symbols.AsReadOnly(), relationships.AsReadOnly(), evidence);
    }
}
