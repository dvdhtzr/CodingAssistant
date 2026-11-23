namespace CodingAssistant.Api;

/// <summary>
/// Read-only drift of two task-scoped, exact-commit imports. Imported graph
/// roots have unrelated random IDs; compare normalized source identities.
/// No fetch, execution, compilation or imagined runtime relationships.
/// </summary>
public sealed record AtlasImportedCommitDriftReport(
    string RepositoryUrl, string FromImportId, string ToImportId,
    string FromCommitSha, string ToCommitSha,
    string FromRevision, string ToRevision,
    int AddedEntities, int RemovedEntities,
    int AddedRelationships, int RemovedRelationships,
    bool Truncated, IReadOnlyList<AtlasArchitectureDriftChange> Changes);

public static class CodeAtlasImportedCommitDrift
{
    private const int MaximumChanges = 100;
    private static bool Relevant(AtlasNode node) => node.Kind is
        "File" or "Project" or "Namespace" or "Class" or "Interface" or
        "Struct" or "Enum" or "Function" or "Method" or "Constructor";

    private static string Qualified(AtlasNode node, string runId)
    {
        var prefix = runId + "|";
        return node.QualifiedName.StartsWith(prefix, StringComparison.Ordinal)
            ? node.QualifiedName[prefix.Length..] : node.QualifiedName;
    }

    private static string Key(AtlasNode node, string runId) =>
        node.Kind + "|" + Qualified(node, runId);

    private static Dictionary<string, AtlasNode> Nodes(AtlasSnapshot graph)
    {
        var output = new Dictionary<string, AtlasNode>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes.Where(Relevant))
            if (!output.TryAdd(Key(node, graph.RunId), node))
                throw new StoreFailure("ATLAS_SNAPSHOT_CORRUPT");
        return output;
    }

    private static Dictionary<string, (AtlasEdge Edge, string Description)>
        Relations(AtlasSnapshot graph)
    {
        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var output = new Dictionary<string,
            (AtlasEdge Edge, string Description)>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges.Where(e =>
            e.Kind is not ("Contains" or "Declares")))
        {
            if (!nodes.TryGetValue(edge.SourceId, out var from) ||
                !nodes.TryGetValue(edge.TargetId, out var to))
                throw new StoreFailure("ATLAS_SNAPSHOT_CORRUPT");
            var description = Qualified(from, graph.RunId) + " → " +
                Qualified(to, graph.RunId);
            var key = edge.Kind + "|" + Key(from, graph.RunId) +
                "|" + Key(to, graph.RunId) + "|" + edge.Resolution;
            output.TryAdd(key, (edge, description));
        }
        return output;
    }

    public static AtlasImportedCommitDriftReport Compare(
        AtlasImportedRepositoryGraph from, AtlasImportedRepositoryGraph to)
    {
        if (from.Repository.Id == to.Repository.Id ||
            from.Repository.CommitSha == to.Repository.CommitSha ||
            from.Repository.TaskId != to.Repository.TaskId ||
            !string.Equals(from.Repository.RepositoryUrl,
                to.Repository.RepositoryUrl, StringComparison.OrdinalIgnoreCase) ||
            from.Graph.RunId != from.Repository.Id ||
            to.Graph.RunId != to.Repository.Id ||
            from.Graph.SourceRevision != from.Repository.SourceRevision ||
            to.Graph.SourceRevision != to.Repository.SourceRevision)
            throw new StoreFailure("ATLAS_IMPORT_DRIFT_INVALID");

        var oldNodes = Nodes(from.Graph);
        var newNodes = Nodes(to.Graph);
        var oldEdges = Relations(from.Graph);
        var newEdges = Relations(to.Graph);
        var addedNodes = newNodes.Keys.Except(oldNodes.Keys,
            StringComparer.Ordinal).ToArray();
        var removedNodes = oldNodes.Keys.Except(newNodes.Keys,
            StringComparer.Ordinal).ToArray();
        var addedEdges = newEdges.Keys.Except(oldEdges.Keys,
            StringComparer.Ordinal).ToArray();
        var removedEdges = oldEdges.Keys.Except(newEdges.Keys,
            StringComparer.Ordinal).ToArray();
        var changes = new List<AtlasArchitectureDriftChange>();

        void NodeChange(string action, AtlasNode node, string runId) =>
            changes.Add(new(action, "Entity", node.Kind,
                Qualified(node, runId), node.Path, node.Line, "SourceStructure"));

        void EdgeChange(string action, (AtlasEdge Edge, string Description) item) =>
            changes.Add(new(action, "Relationship", item.Edge.Kind,
                item.Description, item.Edge.EvidencePath,
                item.Edge.EvidenceLine, item.Edge.Resolution));

        foreach (var key in addedNodes)
            NodeChange("Added", newNodes[key], to.Graph.RunId);
        foreach (var key in removedNodes)
            NodeChange("Removed", oldNodes[key], from.Graph.RunId);
        foreach (var key in addedEdges) EdgeChange("Added", newEdges[key]);
        foreach (var key in removedEdges) EdgeChange("Removed", oldEdges[key]);

        var ordered = changes.OrderBy(c => c.Category, StringComparer.Ordinal)
            .ThenBy(c => c.Operation, StringComparer.Ordinal)
            .ThenBy(c => c.Kind, StringComparer.Ordinal)
            .ThenBy(c => c.Identity, StringComparer.Ordinal)
            .Take(MaximumChanges).ToArray();
        return new(from.Repository.RepositoryUrl,
            from.Repository.Id, to.Repository.Id,
            from.Repository.CommitSha, to.Repository.CommitSha,
            from.Repository.SourceRevision, to.Repository.SourceRevision,
            addedNodes.Length, removedNodes.Length,
            addedEdges.Length, removedEdges.Length,
            changes.Count > MaximumChanges, ordered);
    }
}
