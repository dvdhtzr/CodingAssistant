using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

/// <summary>
/// Immutable planning-only snapshot of exact owner-approved decisions.
/// An architecture snapshot is NEVER a runtime or execution authorization.
/// </summary>
public sealed record DraftArchitectureChoice(
    string Topic, int Revision, string ProposalSha256, string SelectedOptionId,
    string Label, string Benefit, string Tradeoff, string? RuntimeProfileId);

public sealed record DraftArchitectureContract(
    int SchemaVersion, int SpecificationVersion, string SpecificationSha256,
    IReadOnlyList<DraftArchitectureChoice> Choices, string Sha256);

public sealed record DraftArchitectureReadiness(
    string Status, bool ReadyForSourceGeneration,
    string? ArchitectureSha256, IReadOnlyList<string> BlockingTopics,
    IReadOnlyList<DraftArchitectureChoice> ApprovedChoices);

public static class DraftArchitecturePolicy
{
    private static readonly string[] Topics = ["technology-stack", "architecture-style"];

    private sealed record DecisionRow(
        string Topic, int Revision, int SpecificationVersion, string SpecificationSha256,
        string Status, string ProposalSha256, string? SelectedOptionId,
        string OptionsJson);

    private static DecisionRow? Latest(
        SqliteConnection db, SqliteTransaction? tx, string taskId, string topic)
    {
        using var command = TaskStore.Command(db, tx, """
            SELECT topic,revision,specification_version,specification_sha256,
                status,proposal_sha256,selected_option_id,options_json
            FROM architecture_decisions
            WHERE task_id=@task AND topic=@topic
            ORDER BY revision DESC LIMIT 1
            """, ("@task", taskId), ("@topic", topic));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new DecisionRow(reader.GetString(0), reader.GetInt32(1),
                reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7))
            : null;
    }

    private static string ComputeSha256(
        string taskId, int version, string specSha,
        IReadOnlyList<DraftArchitectureChoice> choices)
    {
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, taskId, specificationVersion = version,
            specificationSha256 = specSha, choices
        });
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static (DraftArchitectureContract? Contract, string[] Blocking) Resolve(
        SqliteConnection db, SqliteTransaction? tx, string taskId,
        int version, string specSha)
    {
        var rows = Topics.Select(topic => Latest(db, tx, taskId, topic)).ToArray();
        if (rows.All(row => row is null))
            return (null, Topics);
        var blocked = new List<string>();
        var choices = new List<DraftArchitectureChoice>();
        foreach (var (topic, row) in Topics.Zip(rows))
        {
            if (row is null || row.Status != "Approved" ||
                row.SpecificationVersion != version ||
                row.SpecificationSha256 != specSha ||
                string.IsNullOrWhiteSpace(row.SelectedOptionId))
            {
                blocked.Add(topic);
                continue;
            }
            var alternatives = JsonSerializer.Deserialize<List<ArchitectureOption>>(
                row.OptionsJson);
            var selected = alternatives?.SingleOrDefault(
                option => option.Id == row.SelectedOptionId);
            if (selected is null)
            {
                blocked.Add(topic);
                continue;
            }
            choices.Add(new DraftArchitectureChoice(
                row.Topic, row.Revision, row.ProposalSha256,
                row.SelectedOptionId!, selected.Label, selected.Benefit,
                selected.Tradeoff, selected.RuntimeProfileId));
        }
        if (blocked.Count != 0)
            return (null, blocked.ToArray());

        var digest = ComputeSha256(taskId, version, specSha, choices);
        return (new DraftArchitectureContract(1, version, specSha,
            choices.AsReadOnly(), digest), []);
    }

    // Called inside the same transaction that starts a new source-only job.
    // Zero decisions retain legacy API behavior, but are explicitly UNBOUND.
    // Once discovery begins, partial/stale/deferred choices fail closed.
    internal static DraftArchitectureContract? Capture(
        SqliteConnection db, SqliteTransaction tx, string taskId,
        int version, string specSha)
    {
        var result = Resolve(db, tx, taskId, version, specSha);
        if (result.Contract is not null)
            return result.Contract;
        if (result.Blocking.Length == Topics.Length &&
            Topics.All(topic => Latest(db, tx, taskId, topic) is null))
            return null; // Historical spec-only API, no silent UI fallback.
        throw new StoreFailure("ARCHITECTURE_NOT_READY");
    }

    public static DraftArchitectureReadiness Readiness(
        TaskStore tasks, InterviewStore interviews, string taskId)
    {
        var interview = interviews.Read(taskId);
        var spec = interview.Specification;
        if (spec is null || spec.Status != "Approved")
            return new DraftArchitectureReadiness(
                "SPECIFICATION_NOT_APPROVED", false, null, Topics, []);
        using var db = tasks.Open();
        var result = Resolve(db, null, taskId, spec.Version, spec.Sha256);
        if (result.Contract is not null)
            return new DraftArchitectureReadiness(
                "READY", true, result.Contract.Sha256, [],
                result.Contract.Choices);
        var hasAny = Topics.Any(topic => Latest(db, null, taskId, topic) is not null);
        return new DraftArchitectureReadiness(
            hasAny ? "INCOMPLETE" : "LEGACY_UNBOUND",
            false, null, result.Blocking, []);
    }

    internal static void AssertCurrent(
        SqliteConnection db, SqliteTransaction tx, string taskId,
        int version, string specSha, string? expectedSha)
    {
        DraftArchitectureContract? current;
        try { current = Capture(db, tx, taskId, version, specSha); }
        catch (StoreFailure error) when (error.Code == "ARCHITECTURE_NOT_READY")
        {
            throw new StoreFailure("ARCHITECTURE_CONFLICT");
        }
        if (current?.Sha256 != expectedSha)
            throw new StoreFailure("ARCHITECTURE_CONFLICT");
    }

    // A declared Static Web stack must not silently turn into C#/React/TS.
    // This is bounded plan validation, NOT a browser or compile test.
    private static bool StaticWeb(DraftArchitectureContract? context) =>
        context?.Choices.FirstOrDefault(c =>
            c.Topic == "technology-stack")?.RuntimeProfileId == "static-web";

    // Allowed source types are independent of model self-assessment.
    public static void ValidateSourcePath(
        DraftArchitectureContract? context, string path)
    {
        if (!StaticWeb(context)) return;
        var extension = Path.GetExtension(path);
        if (extension is not (".html" or ".css" or ".js" or ".json" or ".md"))
            throw new StoreFailure("ARCHITECTURE_PLAN_CONFLICT");
    }

    public static void ValidatePlannedFiles(
        DraftArchitectureContract? context, IReadOnlyList<PlannedProjectFile> files)
    {
        if (!StaticWeb(context)) return;
        if (!files.Any(file => string.Equals(file.Path, "index.html",
                StringComparison.OrdinalIgnoreCase)))
            throw new StoreFailure("ARCHITECTURE_PLAN_CONFLICT");
        foreach (var file in files)
            ValidateSourcePath(context, file.Path);
    }

    public static string AsModelInstructions(DraftArchitectureContract context)
    {
        var lines = new List<string>
        {
            "OWNER-APPROVED ARCHITECTURE BINDING (planning-only; not execution permission)",
            "Immutable architecture SHA-256: " + context.Sha256,
            "These exact selected technical choices override any unapproved",
            "technical assumption in the earlier product specification.",
            "Plan and write files consistent with BOTH approved choices.",
            "Do not replace them with your preferred stack/architecture."
        };
        foreach (var choice in context.Choices)
        {
            lines.Add(choice.Topic + ": " + choice.Label +
                " (option " + choice.SelectedOptionId + ")");
            lines.Add("Benefit: " + choice.Benefit);
            lines.Add("Tradeoff: " + choice.Tradeoff);
            lines.Add("Declared runtime profile: " +
                (choice.RuntimeProfileId ?? "none; not qualified"));
        }
        lines.Add("Output remains unverified source only; do not execute anything.");
        return string.Join("\n", lines);
    }
}
