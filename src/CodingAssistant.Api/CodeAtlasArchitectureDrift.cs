namespace CodingAssistant.Api;

/// <summary>
/// M8: read-only, source-revision-bound architecture drift. Historical graph
/// facts are compared by stable identities, never by model narration, source
/// execution or inferred verification results.
/// </summary>
public sealed record AtlasArchitectureDriftChange(
    string Operation, string Category, string Kind, string Identity,
    string? Path, int? Line, string Resolution);

public sealed record AtlasArchitectureDriftReport(
    string RunId, int FromSequence, int ToSequence,
    string FromRevision, string ToRevision,
    int AddedEntities, int RemovedEntities,
    int AddedRelationships, int RemovedRelationships,
    bool Truncated, IReadOnlyList<AtlasArchitectureDriftChange> Changes);

public static class CodeAtlasArchitectureDrift
{
    private const int MaximumChanges = 100;

    private static bool ArchitecturalEntity(AtlasNode node) =>
        node.Kind is "Project" or "Namespace" or "Class" or "Interface" or
            "Struct" or "Enum" or "Function" or "Method" or "Constructor";

    private static bool ArchitecturalRelation(AtlasEdge edge) =>
        edge.Kind is not ("Contains" or "Declares");

    private static string EdgeKey(AtlasEdge edge) =>
        edge.Kind + "|" + edge.SourceId + "|" + edge.TargetId;

    public static AtlasArchitectureDriftReport Compare(
        AtlasReplayFrame from, AtlasReplayFrame to)
    {
        if (from.Sequence < 1 || to.Sequence <= from.Sequence ||
            from.Graph.RunId != to.Graph.RunId ||
            from.Graph.SourceRevision != from.SourceRevision ||
            to.Graph.SourceRevision != to.SourceRevision)
            throw new StoreFailure("INVALID_INPUT");

        var oldNodes = from.Graph.Nodes.ToDictionary(n => n.Id,
            StringComparer.Ordinal);
        var newNodes = to.Graph.Nodes.ToDictionary(n => n.Id,
            StringComparer.Ordinal);
        var oldEntities = oldNodes.Values.Where(ArchitecturalEntity)
            .ToDictionary(n => n.Id, StringComparer.Ordinal);
        var newEntities = newNodes.Values.Where(ArchitecturalEntity)
            .ToDictionary(n => n.Id, StringComparer.Ordinal);
        var oldEdges = from.Graph.Edges.Where(ArchitecturalRelation)
            .ToDictionary(EdgeKey, StringComparer.Ordinal);
        var newEdges = to.Graph.Edges.Where(ArchitecturalRelation)
            .ToDictionary(EdgeKey, StringComparer.Ordinal);

        var additions = newEntities.Keys.Except(oldEntities.Keys,
            StringComparer.Ordinal).ToArray();
        var removals = oldEntities.Keys.Except(newEntities.Keys,
            StringComparer.Ordinal).ToArray();
        var addedEdges = newEdges.Keys.Except(oldEdges.Keys,
            StringComparer.Ordinal).ToArray();
        var removedEdges = oldEdges.Keys.Except(newEdges.Keys,
            StringComparer.Ordinal).ToArray();

        var changes = new List<AtlasArchitectureDriftChange>();

        void Entity(string operation, AtlasNode node) =>
            changes.Add(new(operation, "Entity", node.Kind,
                node.QualifiedName, node.Path, node.Line, "SourceStructure"));

        void Relationship(string operation, AtlasEdge edge,
            IReadOnlyDictionary<string, AtlasNode> nodes)
        {
            // Corrupt historical references must not become invented facts.
            if (!nodes.TryGetValue(edge.SourceId, out var source) ||
                !nodes.TryGetValue(edge.TargetId, out var target))
                throw new StoreFailure("ATLAS_SNAPSHOT_CORRUPT");
            changes.Add(new(operation, "Relationship", edge.Kind,
                source.QualifiedName + " → " + target.QualifiedName,
                edge.EvidencePath, edge.EvidenceLine,
                edge.Resolution));
        }

        foreach (var id in additions) Entity("Added", newEntities[id]);
        foreach (var id in removals) Entity("Removed", oldEntities[id]);
        foreach (var key in addedEdges)
            Relationship("Added", newEdges[key], newNodes);
        foreach (var key in removedEdges)
            Relationship("Removed", oldEdges[key], oldNodes);

        var ordered = changes.OrderBy(c => c.Category, StringComparer.Ordinal)
            .ThenBy(c => c.Operation, StringComparer.Ordinal)
            .ThenBy(c => c.Kind, StringComparer.Ordinal)
            .ThenBy(c => c.Identity, StringComparer.Ordinal)
            .Take(MaximumChanges).ToArray();

        return new(from.Graph.RunId, from.Sequence, to.Sequence,
            from.SourceRevision, to.SourceRevision, additions.Length,
            removals.Length, addedEdges.Length, removedEdges.Length,
            changes.Count > MaximumChanges, ordered);
    }
}
