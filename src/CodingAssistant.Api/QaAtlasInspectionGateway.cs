namespace CodingAssistant.Api;

/// <summary>
/// Host-brokered, read-only QA investigation of exactly the currently
/// reviewed source file. The agent selects a validated operation; the host
/// resolves it from the immutable source snapshot. No native model tools,
/// cross-file contents, execution or source-write capability are available.
/// </summary>
public static class QaAtlasInspectionGateway
{
    public static AtlasInspectionResult Execute(
        QaReviewInput input, AtlasInspectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(request);
        if (input.SourceRun.Id.Length != 32 ||
            !QaReviewStore.IsReviewableSource(
                input.SourceRun.State, input.AllFiles.Count) ||
            input.AllFiles.Count > EngineeringRunStore.MaxDistinctFiles ||
            input.AllFiles.Any(f =>
                f.Content.Length > ProjectDraftStore.MaxFileCharacters) ||
            !input.AllFiles.Any(f => f.Path == input.File.Path &&
                f.Sha256 == input.File.Sha256 &&
                f.Content == input.File.Content) ||
            request.Name is not ("atlas.find_symbols" or
                "atlas.get_relationships" or "atlas.source_excerpt") ||
            string.IsNullOrWhiteSpace(request.Argument) ||
            request.Argument.Length > 80 ||
            request.Argument.Any(char.IsControl))
            throw new StoreFailure("INVALID_INPUT");

        var tools = new CodeAtlasAgentReadOnlyTools(
            input.SourceRun.Id, input.AllFiles, input.File.Path);
        var symbols = Array.Empty<AtlasAgentSymbol>();
        var relationships = Array.Empty<AtlasAgentRelationship>();
        AtlasAgentSourceEvidence? evidence = null;
        switch (request.Name)
        {
            case "atlas.find_symbols":
                symbols = tools.FindSymbols(request.Argument).ToArray();
                break;
            case "atlas.get_relationships":
                relationships = tools.GetRelationships(request.Argument)
                    .Take(16).ToArray();
                break;
            case "atlas.source_excerpt":
                evidence = tools.GetSourceExcerpt(request.Argument);
                break;
        }
        return new AtlasInspectionResult(
            request.Name, tools.SourceRevision, tools.Coverage,
            symbols.Length + relationships.Length + (evidence is null ? 0 : 1),
            symbols, relationships, evidence);
    }
}
