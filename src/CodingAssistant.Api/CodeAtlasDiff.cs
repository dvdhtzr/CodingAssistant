namespace CodingAssistant.Api;

/// <summary>
/// Source-backed structural differences between two committed graph snapshots.
/// IDs and relationship keys contain no executable source text. An "updated"
/// node denotes a touched source file, not verified semantic behavior.
/// </summary>
public sealed record AtlasGraphDelta(
    IReadOnlyList<string> AddedNodeIds,
    IReadOnlyList<string> UpdatedNodeIds,
    IReadOnlyList<string> RemovedNodeIds,
    IReadOnlyList<string> AddedEdgeKeys,
    IReadOnlyList<string> RemovedEdgeKeys);

public static class CodeAtlasDiff
{
    private static string Key(AtlasEdge edge) =>
        edge.Kind + "|" + edge.SourceId + "|" + edge.TargetId;

    public static AtlasGraphDelta Between(
        AtlasSnapshot before, AtlasSnapshot after,
        IEnumerable<string> modifiedPaths)
    {
        var oldNodes = new HashSet<string>(
            before.Nodes.Select(node => node.Id), StringComparer.Ordinal);
        var newNodes = new HashSet<string>(
            after.Nodes.Select(node => node.Id), StringComparer.Ordinal);
        var touched = new HashSet<string>(modifiedPaths,
            StringComparer.OrdinalIgnoreCase);
        var oldEdges = new HashSet<string>(
            before.Edges.Select(Key), StringComparer.Ordinal);
        var newEdges = new HashSet<string>(
            after.Edges.Select(Key), StringComparer.Ordinal);

        return new(
            newNodes.Except(oldNodes).Order(StringComparer.Ordinal).ToArray(),
            after.Nodes.Where(node => node.Path is not null &&
                    oldNodes.Contains(node.Id) && touched.Contains(node.Path))
                .Select(node => node.Id).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray(),
            oldNodes.Except(newNodes).Order(StringComparer.Ordinal).ToArray(),
            newEdges.Except(oldEdges).Order(StringComparer.Ordinal).ToArray(),
            oldEdges.Except(newEdges).Order(StringComparer.Ordinal).ToArray());
    }
}
