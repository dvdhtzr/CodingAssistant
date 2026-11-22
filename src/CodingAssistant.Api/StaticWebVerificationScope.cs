using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodingAssistant.Api;

/// <summary>
/// Read-only, deterministic scope candidate from a persisted MAF Engineering run.
/// It is NEVER an executable approval, a source download, or a sandbox credential.
/// Existing RuntimeProfileRegistry static-web UNQUALIFIED remains authoritative.
/// </summary>
public sealed record StaticWebScopeFile(string Path, string Sha256, int Utf8Bytes);

public sealed record StaticWebVerificationScopePreview(
    int SchemaVersion, string TaskId, string EngineeringRunId,
    int PlanRevision, string PlanSha256,
    int SpecificationVersion, string SpecificationSha256,
    string ArchitectureSha256, string Model,
    string? SourceFingerprint, string? ScopeSha256,
    string Status, bool ExecutionEnabled, string VerificationStatus,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<StaticWebScopeFile> Files);

public static class StaticWebVerificationScope
{
    public const int SchemaVersion = 1;
    private static readonly HashSet<string> AllowedExtensions = new(
        [".html", ".css", ".js", ".json"], StringComparer.OrdinalIgnoreCase);

    private static string Sha(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    /// <summary>
    /// No filesystem access or model calls. The snapshot comes from the
    /// authenticated task-specific EngineeringRunStore; latestSource must be
    /// checked independently by its host caller, never supplied by a model.
    /// </summary>
    public static StaticWebVerificationScopePreview Preview(
        EngineeringRunSnapshot snapshot,
        ImplementationPlan? currentPlan,
        InterviewSpecification? currentSpecification,
        DraftArchitectureReadiness architecture,
        bool latestSource)
    {
        var run = snapshot.Job;
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        var safeFiles = new List<StaticWebScopeFile>();
        string? fingerprint = null;
        string? digest = null;

        if (run.State != "Generated")
            problems.Add("SOURCE_NOT_COMPLETE");
        if (!latestSource)
            problems.Add("SOURCE_NOT_LATEST");
        if (currentPlan is null || currentPlan.Status != "Approved" ||
            !currentPlan.Current || currentPlan.TaskId != run.TaskId ||
            currentPlan.Revision != run.PlanRevision ||
            currentPlan.PlanSha256 != run.PlanSha256)
            problems.Add("APPROVED_PLAN_MISMATCH");
        if (currentSpecification is null ||
            currentSpecification.Status != "Approved" ||
            currentSpecification.Version != run.SpecificationVersion ||
            currentSpecification.Sha256 != run.SpecificationSha256)
            problems.Add("APPROVED_SPECIFICATION_MISMATCH");
        if (!architecture.ReadyForSourceGeneration ||
            architecture.ArchitectureSha256 != run.ArchitectureSha256)
            problems.Add("APPROVED_ARCHITECTURE_MISMATCH");
        if (!architecture.ApprovedChoices.Any(c =>
            c.Topic == "technology-stack" &&
            c.RuntimeProfileId == "static-web"))
            problems.Add("NOT_STATIC_WEB_PROFILE");
        if (currentPlan is not null && currentPlan.Model != run.Model)
            problems.Add("MODEL_BINDING_CHANGED");
        if (snapshot.Files.Count is < 1 or > EngineeringRunStore.MaxDistinctFiles)
            problems.Add("SOURCE_FILE_COUNT_OUT_OF_BOUNDS");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var file in snapshot.Files.Take(EngineeringRunStore.MaxDistinctFiles + 1))
        {
            var safe = true;
            try
            {
                if (ProjectDraftStore.SafePath(file.Path) != file.Path)
                    safe = false;
            }
            catch (StoreFailure) { safe = false; }
            if (!safe || !seen.Add(file.Path))
            {
                problems.Add("UNSAFE_OR_DUPLICATE_FILE_PATH");
                continue;
            }
            if (!AllowedExtensions.Contains(Path.GetExtension(file.Path)))
            {
                problems.Add("SOURCE_EXTENSION_OUT_OF_PROFILE");
                continue;
            }
            var content = file.Content ?? string.Empty;
            var byteLength = Encoding.UTF8.GetByteCount(content);
            totalBytes += byteLength;
            if (byteLength is < 1 or > 65536 || content.IndexOf('\0') >= 0)
            {
                problems.Add("SOURCE_FILE_BOUNDS_INVALID");
                continue;
            }
            var computed = Sha(content);
            if (file.Sha256 != computed)
            {
                problems.Add("SOURCE_FILE_HASH_MISMATCH");
                continue;
            }
            safeFiles.Add(new StaticWebScopeFile(file.Path, computed, byteLength));
        }
        if (totalBytes > 160000 || snapshot.Files.Sum(f => (long)(f.Content?.Length ?? 0))
            > EngineeringRunStore.MaxTotalCharacters)
            problems.Add("SOURCE_TOTAL_BOUNDS_INVALID");
        if (!seen.Contains("index.html"))
            problems.Add("MISSING_ROOT_INDEX_HTML");

        // The original source preflight is advisory. Even its warning findings
        // must be resolved before a source snapshot can be a scope candidate.
        // It never executes JavaScript or Chromium.
        if (problems.Count == 0)
        {
            var preflight = StaticWebSourcePreflight.Inspect(snapshot);
            if (preflight.Findings.Count > 0 ||
                preflight.Status != "NO_ISSUES_DETECTED")
                problems.Add("STATIC_SOURCE_PREFLIGHT_HAS_FINDINGS");
        }

        if (problems.Count == 0)
        {
            fingerprint = QaReviewStore.Fingerprint(snapshot.Files);
            var ordered = safeFiles.OrderBy(f => f.Path, StringComparer.Ordinal)
                .ToArray();
            // Hash the complete *scope descriptor*, including the task,
            // owner-approved plan, architecture, exact files and their hashes.
            // This is a non-secret fingerprint, NOT an authorization token.
            var exact = JsonSerializer.Serialize(new
            {
                schemaVersion = SchemaVersion, taskId = run.TaskId,
                runId = run.Id, run.PlanRevision, run.PlanSha256,
                run.SpecificationVersion, run.SpecificationSha256,
                run.ArchitectureSha256, run.Model,
                sourceFingerprint = fingerprint, files = ordered
            });
            digest = Sha(exact);
        }

        return new StaticWebVerificationScopePreview(
            SchemaVersion, run.TaskId, run.Id, run.PlanRevision,
            run.PlanSha256, run.SpecificationVersion, run.SpecificationSha256,
            run.ArchitectureSha256, run.Model, fingerprint, digest,
            problems.Count == 0 ? "SCOPE_CANDIDATE" : "BLOCKED",
            ExecutionEnabled: false,
            VerificationStatus: "GENERATED_NOT_TESTED",
            problems.ToArray(), safeFiles.OrderBy(f => f.Path, StringComparer.Ordinal)
                .ToArray());
    }
}
