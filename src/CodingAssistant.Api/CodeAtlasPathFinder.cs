namespace CodingAssistant.Api;

/// <summary>
/// Bounded source-grounded traversal. Directed edges are followed only in
/// their declared direction; no missing runtime calls are inferred.
/// </summary>
public sealed record AtlasPathResult(
    string FromId, string ToId, bool Found,
    IReadOnlyList<string> NodeIds, IReadOnlyList<AtlasEdge> Edges,
    int Visited, bool Truncated);

public static class CodeAtlasPathFinder
{
    private static readonly HashSet<string> NavigableKinds = new(
        ["Calls", "Inherits", "Implements", "ConstructorDependsOn",
         "ProjectReferences", "ModuleImports", "MarkupReferences",
         "Contains", "Declares"],
        StringComparer.Ordinal);

    public static AtlasPathResult Trace(AtlasSnapshot graph, string fromId,
        string toId, int maxHops = 6, int maxVisited = 400)
    {
        if (maxHops is < 1 or > 12 || maxVisited is < 2 or > 4000 ||
            string.IsNullOrWhiteSpace(fromId) ||
            string.IsNullOrWhiteSpace(toId))
            throw new StoreFailure("INVALID_INPUT");
        var nodes = graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        if (!nodes.Contains(fromId) || !nodes.Contains(toId))
            throw new StoreFailure("NOT_FOUND");
        if (fromId == toId)
            return new(fromId, toId, true, [fromId], [], 1, false);

        var links = graph.Edges.Where(e => NavigableKinds.Contains(e.Kind) &&
                nodes.Contains(e.SourceId) && nodes.Contains(e.TargetId))
            .GroupBy(e => e.SourceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g
                .OrderBy(e => e.Kind, StringComparer.Ordinal)
                .ThenBy(e => e.TargetId, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        var previous = new Dictionary<string, (string Parent, AtlasEdge Edge)>(
            StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { fromId };
        var queue = new Queue<(string NodeId, int Hops)>();
        queue.Enqueue((fromId, 0));
        var truncated = false;
        while (queue.Count != 0)
        {
            var (current, hops) = queue.Dequeue();
            if (hops >= maxHops) continue;
            if (!links.TryGetValue(current, out var edges)) continue;
            foreach (var edge in edges)
            {
                if (visited.Contains(edge.TargetId)) continue;
                if (visited.Count >= maxVisited)
                {
                    truncated = true;
                    break;
                }
                visited.Add(edge.TargetId);
                previous[edge.TargetId] = (current, edge);
                if (edge.TargetId == toId)
                {
                    var pathEdges = new List<AtlasEdge>();
                    var nodeIds = new List<string> { toId };
                    var at = toId;
                    while (at != fromId)
                    {
                        var step = previous[at];
                        pathEdges.Add(step.Edge);
                        at = step.Parent;
                        nodeIds.Add(at);
                    }
                    pathEdges.Reverse();
                    nodeIds.Reverse();
                    return new(fromId, toId, true, nodeIds, pathEdges,
                        visited.Count, false);
                }
                queue.Enqueue((edge.TargetId, hops + 1));
            }
            if (truncated) break;
        }
        return new(fromId, toId, false, [], [], visited.Count, truncated);
    }
}
