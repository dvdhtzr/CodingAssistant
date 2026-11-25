namespace CodingAssistant.Api;

public sealed record QaCriterionIdentity(string Id, string WorkItemId,
    string WorkItemTitle, string Requirement);
public sealed record QaFindingGroup(string Path, int Index, QaFinding Finding,
    string? CriterionId, IReadOnlyList<int> OriginalIndices);
public sealed record QaCriterionCoverage(QaCriterionIdentity Criterion,
    string StaticStatus, string VerificationStatus, int ConcernCount);
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
                row.Finding.Evidence, row.Finding.Recommendation))
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
            return new QaCriterionCoverage(criterion,
                count > 0 ? "StaticConcern" : "NoLinkedConcern",
                "RequiresTesting", count);
        }).ToArray();
        return new QaAcceptanceSummary(plan.PlanSha256, review.SourceSha256,
            review.Id, coverage, groups, raw.Length,
            groups.Count(row => row.CriterionId is null));
    }
}
