namespace CodingAssistant.Api;

public sealed record QaCriterionIdentity(string Id, string WorkItemId,
    string WorkItemTitle, string Requirement);
public sealed record QaFindingGroup(string Path, int Index, QaFinding Finding,
    string? CriterionId, IReadOnlyList<int> OriginalIndices);
public sealed record QaImplementationIndicator(string Path, string SourceSha256,
    string SourceExcerpt, string Explanation);
public sealed record QaCriterionCoverage(QaCriterionIdentity Criterion,
    string StaticStatus, string VerificationStatus, int ConcernCount,
    IReadOnlyList<QaImplementationIndicator> ImplementationIndicators);
public sealed record QaAcceptanceSummary(string PlanSha256, string SourceSha256,
    string ReviewId, IReadOnlyList<QaCriterionCoverage> Criteria,
    IReadOnlyList<QaFindingGroup> Findings, int RawFindingCount,
    int UnmappedFindingCount);

/// <summary>
/// Deterministic, read-only projection of the exact approved plan and recorded
/// QA hypotheses. No semantic inference, execution, or fabricated pass/fail.
/// </summary>
public static class QaAcceptanceProjection
{
    // IDs are stable within an immutable approved plan SHA-256. Identical
    // wording in different work items does not make those criteria identical.
    public static IReadOnlyList<QaCriterionIdentity> Identify(ImplementationPlan plan) =>
        plan.Outline.Tasks.SelectMany(item => item.AcceptanceCriteria.Select((text, i) =>
            new QaCriterionIdentity(item.Id + ":" + (i + 1).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
                item.Id, item.Title, text))).ToArray();

    private static string Normalize(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? Resolve(QaFinding finding, IReadOnlyList<QaCriterionIdentity> criteria)
    {
        // Existing reports have no explicit ID. Only unambiguous exact text
        // (ignoring case/whitespace) is eligible for a retrospective match.
        var candidates = criteria.Where(c => string.Equals(Normalize(c.Requirement),
            Normalize(finding.Requirement), StringComparison.OrdinalIgnoreCase));
        if (finding.CriterionId is not null)
            candidates = candidates.Where(c => c.Id == finding.CriterionId);
        var matches = candidates.Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Id : null;
    }

    // The store supplies the exact saved primary file content, not model text.
    // Literal grounding is not a semantic proof; positive evidence cannot
    // become a repair, approval or test result.
    public static QaFileAssessment Ground(ImplementationPlan plan, string source,
        QaFileAssessment assessment)
    {
        var ids = Identify(plan).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        bool HasExcerpt(string? text) => text is { Length: >= 8 and <= 240 } &&
            !string.IsNullOrWhiteSpace(text) && source.Contains(text, StringComparison.Ordinal);
        return assessment with {
            Findings = assessment.Findings.Select(f => HasExcerpt(f.SourceExcerpt)
                ? f : f with { SourceExcerpt = null }).ToArray(),
            ImplementationEvidence = (assessment.ImplementationEvidence ?? [])
                .Where(item => ids.Contains(item.CriterionId) && HasExcerpt(item.SourceExcerpt))
                .DistinctBy(item => (item.CriterionId, item.SourceExcerpt)).ToArray()
        };
    }

    public static QaAcceptanceSummary Build(QaReviewRun review, ImplementationPlan plan,
        IReadOnlyList<QaFileReport> reports)
    {
        if (plan.TaskId != review.TaskId || plan.Revision != review.PlanRevision ||
            plan.PlanSha256 != review.PlanSha256 ||
            plan.SpecificationVersion != review.SpecificationVersion ||
            plan.SpecificationSha256 != review.SpecificationSha256 ||
            plan.ArchitectureSha256 != review.ArchitectureSha256 ||
            plan.ApprovedUtc is null)
            throw new StoreFailure("SOURCE_CHANGED");

        var identities = Identify(plan);
        var raw = reports.OrderBy(r => r.Path, StringComparer.Ordinal)
            .SelectMany(report => report.Assessment.Findings.Select((finding, index) =>
                new QaFindingGroup(report.Path, index, finding,
                    Resolve(finding, identities), [index]))).ToArray();
        // Consolidate only exact duplicate observations in the SAME file.
        // Different evidence, proposed fixes, mapped criteria or file paths
        // remain independent. Never collapse merely similar natural language.
        var groups = raw.GroupBy(row => (
                row.Path, row.CriterionId, row.Finding.Requirement,
                row.Finding.Evidence, row.Finding.Recommendation, row.Finding.SourceExcerpt))
            .Select(group => {
                var representative = group.OrderByDescending(row =>
                    row.Finding.Severity switch { "Critical" => 3, "Major" => 2, _ => 1 })
                    .ThenBy(row => row.Index).First();
                return representative with {
                    OriginalIndices = group.Select(row => row.Index).Order().ToArray()
                };
            }).OrderBy(row => row.Path, StringComparer.Ordinal)
            .ThenBy(row => row.Index).ToArray();

        var coverage = identities.Select(criterion => {
            var count = groups.Count(row => row.CriterionId == criterion.Id);
            var indicators = reports.OrderBy(r => r.Path, StringComparer.Ordinal)
                .SelectMany(report => (report.Assessment.ImplementationEvidence ?? [])
                    .Where(item => item.CriterionId == criterion.Id)
                    .Select(item => new QaImplementationIndicator(report.Path,
                        report.SourceSha256, item.SourceExcerpt, item.Explanation)))
                .DistinctBy(item => (item.Path, item.SourceExcerpt)).ToArray();
            return new QaCriterionCoverage(criterion,
                count > 0 ? (indicators.Length > 0 ? "EvidenceAndConcern" : "StaticConcern")
                    : (indicators.Length > 0 ? "ImplementationEvidence" : "NoLinkedConcern"),
                "RequiresTesting", count, indicators);
        }).ToArray();
        return new QaAcceptanceSummary(plan.PlanSha256, review.SourceSha256,
            review.Id, coverage, groups, raw.Length,
            groups.Count(row => row.CriterionId is null));
    }
}
