namespace CodingAssistant.Api;

/// <summary>
/// Read-only evidence overlay for the exact engineering source revision.
/// QA findings remain unverified static-review hypotheses. A repair attempt
/// or QA "Reviewed" state never implies successful functional testing.
/// </summary>
public sealed record AtlasQaFindingMarker(
    string Id, string FileNodeId, string Path, int Index,
    string Severity, string Requirement, string Evidence,
    string Recommendation, string Classification, string QaReviewId);
public sealed record AtlasRepairMarker(
    string CycleId, string BaseRunId, string RepairRunId, string State,
    string? QaReviewId, IReadOnlyList<string> FileNodeIds);
public sealed record AtlasEvidenceOverlay(
    string RunId, string SourceRevision,
    string VerificationStatus, string QaReviewStatus,
    string? QaReviewId,
    IReadOnlyList<AtlasQaFindingMarker> Findings,
    IReadOnlyList<AtlasRepairMarker> Repairs,
    IReadOnlyList<string> Diagnostics);

public static class CodeAtlasEvidenceProjector
{
    public static AtlasEvidenceOverlay Project(
        EngineeringRunSnapshot source, AtlasSnapshot graph,
        QaReviewSnapshot? review, IReadOnlyList<RepairCycle> cycles)
    {
        if (source.Job.Id != graph.RunId)
            throw new StoreFailure("INVALID_INPUT");

        var diagnostics = new List<string>();
        var findings = new List<AtlasQaFindingMarker>();
        var fileNodes = graph.Nodes.Where(node =>
                node.Kind == "File" && node.Path is not null)
            .ToDictionary(node => node.Path!, node => node,
                StringComparer.OrdinalIgnoreCase);
        var fingerprints = source.Files.ToDictionary(file => file.Path,
            file => file.Sha256, StringComparer.OrdinalIgnoreCase);
        string qaStatus = "NOT_REVIEWED";
        string? qaId = null;
        if (review is not null)
        {
            if (review.Job.TaskId != source.Job.TaskId ||
                review.Job.EngineeringRunId != source.Job.Id ||
                source.Files.Count == 0 ||
                review.Job.SourceSha256 != QaReviewStore.Fingerprint(source.Files))
            {
                diagnostics.Add("QA_REVIEW_FOR_DIFFERENT_SOURCE_REVISION");
            }
            else
            {
                qaStatus = review.Job.State switch
                {
                    "Reviewed" => "STATIC_REVIEW_ONLY",
                    "Running" => "STATIC_REVIEW_IN_PROGRESS",
                    "Failed" => "STATIC_REVIEW_FAILED",
                    "Interrupted" => "STATIC_REVIEW_INTERRUPTED",
                    _ => "NOT_REVIEWED"
                };
                qaId = review.Job.Id;
                foreach (var report in review.Reports.OrderBy(r => r.Path,
                    StringComparer.Ordinal))
                {
                    if (!fingerprints.TryGetValue(report.Path, out var hash) ||
                        hash != report.SourceSha256 ||
                        !fileNodes.TryGetValue(report.Path, out var fileNode))
                    {
                        diagnostics.Add("QA_FILE_EVIDENCE_MISMATCH:" + report.Path);
                        continue;
                    }
                    for (var i = 0; i < report.Assessment.Findings.Count; i++)
                    {
                        var finding = report.Assessment.Findings[i];
                        findings.Add(new(
                            review.Job.Id + ":" + report.Path + ":" + i,
                            fileNode.Id, report.Path, i, finding.Severity,
                            finding.Requirement, finding.Evidence,
                            finding.Recommendation, "UNVERIFIED_QA_HYPOTHESIS",
                            review.Job.Id));
                    }
                }
            }
        }

        var repairs = new List<AtlasRepairMarker>();
        foreach (var cycle in cycles
            .Where(c => c.TaskId == source.Job.TaskId &&
                (c.BaseRunId == source.Job.Id ||
                 c.EngineeringRunId == source.Job.Id))
            .OrderBy(c => c.CreatedUtc, StringComparer.Ordinal)
            .ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            var ids = cycle.Findings.Select(f =>
                    fileNodes.TryGetValue(f.Path, out var node) ?
                        node.Id : null)
                .Where(id => id is not null)
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            repairs.Add(new(cycle.Id, cycle.BaseRunId,
                cycle.EngineeringRunId, cycle.State,
                cycle.QaReviewId, ids));
        }

        // No source-only generation or MAF QA review may assert tests passed.
        return new(source.Job.Id, graph.SourceRevision, "NOT_TESTED",
            qaStatus, qaId, findings.AsReadOnly(),
            repairs.AsReadOnly(), diagnostics.AsReadOnly());
    }
}

/// <summary>
/// The only data source is the existing task-bound, owner-private engineering
/// and QA stores. No source execution, agent authority or approval is added.
/// </summary>
public sealed class CodeAtlasEvidenceService(
    EngineeringRunStore engineering, QaReviewStore qa,
    RepairCycleStore repairs)
{
    public AtlasEvidenceOverlay Read(string taskId, string runId)
    {
        var source = engineering.Get(taskId, runId);
        var graph = CodeAtlasIndexer.Index(source);
        var latest = qa.Latest(taskId);
        return CodeAtlasEvidenceProjector.Project(source, graph,
            latest, repairs.History(taskId));
    }
}
