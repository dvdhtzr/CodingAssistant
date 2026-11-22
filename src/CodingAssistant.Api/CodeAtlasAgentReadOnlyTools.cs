using System.ComponentModel;

namespace CodingAssistant.Api;

public sealed record AtlasAgentSymbol(
    string Id, string Kind, string Name, string Path, int? Line);
public sealed record AtlasAgentRelationship(
    string Direction, string Kind, string RelatedSymbolId,
    string RelatedSymbol, string? EvidencePath, int? EvidenceLine,
    string Resolution);
public sealed record AtlasAgentSourceEvidence(
    string SymbolId, string Path, int StartLine, string Excerpt);

/// <summary>
/// Narrow host-owned tools for an independent QA agent. Every method is
/// read-only, bounded, and restricted to the ONE exact source file that QA
/// was already authorized to inspect. No filesystem, network, process,
/// project build, source modification or other-run capability exists here.
/// </summary>
public sealed class CodeAtlasAgentReadOnlyTools
{
    private readonly AtlasSnapshot _graph;
    private readonly EngineeringSourceFile _file;
    private readonly Dictionary<string, AtlasNode> _nodes;

    public CodeAtlasAgentReadOnlyTools(string runId,
        IReadOnlyList<EngineeringSourceFile> files,
        string reviewedPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        _file = files.SingleOrDefault(file => file.Path == reviewedPath)
            ?? throw new StoreFailure("INVALID_INPUT");
        if (string.IsNullOrWhiteSpace(runId))
            throw new StoreFailure("INVALID_INPUT");
        _graph = CodeAtlasIndexer.Index(runId, files);
        _nodes = _graph.Nodes.Where(node =>
            node.Path == reviewedPath && node.Kind is not "Repository")
            .ToDictionary(node => node.Id, node => node,
                StringComparer.Ordinal);
    }

    [Description("Find up to 12 declared symbols in the current QA-reviewed file only. This is static source structure, not runtime verification.")]
    public IReadOnlyList<AtlasAgentSymbol> FindSymbols(string query)
    {
        if (query is null || query.Length is < 1 or > 80 ||
            query.Any(char.IsControl))
            return [];
        return _nodes.Values
            .Where(node => node.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                node.QualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(node => node.QualifiedName, StringComparer.Ordinal)
            .Take(12)
            .Select(node => new AtlasAgentSymbol(node.Id, node.Kind,
                node.Name, _file.Path, node.Line))
            .ToArray();
    }

    [Description("List up to 20 actual incoming and outgoing static relationships for a symbol in the current QA-reviewed file. Other files are never exposed.")]
    public IReadOnlyList<AtlasAgentRelationship> GetRelationships(
        string symbolId)
    {
        if (symbolId is null || !_nodes.ContainsKey(symbolId)) return [];
        return _graph.Edges
            .Where(edge => edge.SourceId == symbolId ||
                           edge.TargetId == symbolId)
            .Select(edge =>
            {
                var otherId = edge.SourceId == symbolId ?
                    edge.TargetId : edge.SourceId;
                return _nodes.TryGetValue(otherId, out var other)
                    ? new AtlasAgentRelationship(
                        edge.SourceId == symbolId ? "Outgoing" : "Incoming",
                        edge.Kind, otherId, other.Name,
                        edge.EvidencePath == _file.Path ?
                            edge.EvidencePath : null,
                        edge.EvidencePath == _file.Path ?
                            edge.EvidenceLine : null,
                        edge.Resolution)
                    : null;
            })
            .Where(edge => edge is not null)
            .Take(20)
            .Select(edge => edge!)
            .ToArray();
    }

    [Description("Return a short, line-numbered source excerpt for a declared symbol in the ONE QA-reviewed file, never another file or historical revision.")]
    public AtlasAgentSourceEvidence? GetSourceExcerpt(string symbolId)
    {
        if (symbolId is null ||
            !_nodes.TryGetValue(symbolId, out var node) ||
            node.Line is null)
            return null;
        var lines = _file.Content.Split('\n');
        var begin = Math.Max(0, node.Line.Value - 2);
        var excerpt = string.Join("\n", lines.Skip(begin).Take(8)
            .Select((line, index) =>
                (begin + index + 1).ToString() + ": " +
                line.TrimEnd('\r')));
        if (excerpt.Length > 1200) excerpt = excerpt[..1200];
        return new(symbolId, _file.Path, begin + 1, excerpt);
    }
}
