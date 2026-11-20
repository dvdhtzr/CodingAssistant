using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

// Phase 2 is planning only. An architecture approval is NOT execution consent.
public sealed record ArchitectureOption(
    string Id, string Label, string Benefit, string Tradeoff,
    string? RuntimeProfileId);

public sealed record ProposeArchitectureDecisionRequest(
    int SchemaVersion, int SpecificationVersion, string? SpecificationSha256,
    string? Topic, string? RecommendedOptionId, string? Rationale,
    IReadOnlyList<ArchitectureOption>? Options, int ExpectedRevision);

public sealed record ReviewArchitectureDecisionRequest(
    int SchemaVersion, int ExpectedRevision, string? ProposalSha256,
    string? Action, string? SelectedOptionId, string? Confirmation);

public sealed record ArchitectureDecision(
    int SchemaVersion, string TaskId, string Topic, int Revision,
    int SpecificationVersion, string SpecificationSha256,
    string RecommendedOptionId, string Rationale,
    IReadOnlyList<ArchitectureOption> Options, string ProposalSha256,
    string Status, string? SelectedOptionId, string ProposedUtc,
    string? ReviewedUtc, bool SpecificationCurrent, string EffectiveStatus);

/// <summary>
/// Append-only proposal revisions with separately approved or deferred
/// decisions. All writes are serializable SQLite transactions. No executor
/// looks at this table and it cannot change M1 TaskBuildStore allowlists.
/// </summary>
public sealed class ArchitectureDecisionStore(TaskStore tasks)
{
    public const int SchemaVersion = 1;
    private static readonly Regex Identifier = new(
        "^[a-z0-9][a-z0-9-]{1,47}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TaskIdPattern = new(
        "^[0-9a-f]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HashPattern = new(
        "^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS architecture_decisions(
                task_id TEXT NOT NULL REFERENCES tasks(id),
                topic TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK(revision > 0),
                schema_version INTEGER NOT NULL CHECK(schema_version = 1),
                specification_version INTEGER NOT NULL CHECK(specification_version > 0),
                specification_sha256 TEXT NOT NULL,
                recommended_option_id TEXT NOT NULL,
                rationale TEXT NOT NULL,
                options_json TEXT NOT NULL,
                proposal_sha256 TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN
                    ('Proposed','Approved','Deferred','Superseded')),
                selected_option_id TEXT,
                proposed_utc TEXT NOT NULL,
                reviewed_utc TEXT,
                PRIMARY KEY(task_id, topic, revision)
            );
            CREATE INDEX IF NOT EXISTS idx_architecture_decisions_task
                ON architecture_decisions(task_id, topic, revision);
            """);
    }

    private static void ValidateTaskId(string id)
    {
        if (id is null || !TaskIdPattern.IsMatch(id))
            throw new StoreFailure("INVALID_ID");
    }

    private static string ValidId(string? id)
    {
        if (id is null || !Identifier.IsMatch(id))
            throw new StoreFailure("INVALID_INPUT");
        return id;
    }

    private static string ValidText(string? value, int max)
    {
        if (value is null || value.Trim().Length is < 2 ||
            value.Trim().Length > max ||
            value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new StoreFailure("INVALID_INPUT");
        return value.Trim();
    }

    private sealed record Spec(int Version, string Hash, string Status);

    private static Spec? LatestSpec(
        SqliteConnection db, SqliteTransaction? tx, string taskId)
    {
        using var cmd = TaskStore.Command(db, tx, """
            SELECT version,sha256,status FROM interview_specs
            WHERE task_id=@task ORDER BY version DESC LIMIT 1
            """, ("@task", taskId));
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new Spec(reader.GetInt32(0), reader.GetString(1),
                reader.GetString(2))
            : null;
    }

    private static Spec RequireApprovedSpec(
        SqliteConnection db, SqliteTransaction tx, string taskId,
        int version, string? hash)
    {
        if (version < 1 || hash is null || !HashPattern.IsMatch(hash))
            throw new StoreFailure("INVALID_INPUT");
        var spec = LatestSpec(db, tx, taskId);
        if (spec is null || spec.Status != "Approved")
            throw new StoreFailure("SPECIFICATION_NOT_APPROVED");
        if (spec.Version != version || spec.Hash != hash)
            throw new StoreFailure("SPECIFICATION_CONFLICT");
        return spec;
    }

    private static ArchitectureDecision ReadRow(SqliteDataReader reader)
    {
        var options = JsonSerializer.Deserialize<List<ArchitectureOption>>(
            reader.GetString(8)) ??
            throw new InvalidOperationException("Invalid stored architecture options");
        return new ArchitectureDecision(reader.GetInt32(3), reader.GetString(0),
            reader.GetString(1), reader.GetInt32(2), reader.GetInt32(4),
            reader.GetString(5), reader.GetString(6), reader.GetString(7),
            options.AsReadOnly(), reader.GetString(9), reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13),
            false, "UNKNOWN");
    }

    private const string SelectColumns = """
        SELECT task_id,topic,revision,schema_version,specification_version,
               specification_sha256,recommended_option_id,rationale,
               options_json,proposal_sha256,status,selected_option_id,
               proposed_utc,reviewed_utc
        FROM architecture_decisions
        """;

    private static ArchitectureDecision? Latest(
        SqliteConnection db, SqliteTransaction? tx, string id, string topic)
    {
        using var cmd = TaskStore.Command(db, tx, SelectColumns + """
             WHERE task_id=@task AND topic=@topic
             ORDER BY revision DESC LIMIT 1
            """, ("@task", id), ("@topic", topic));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    private static ArchitectureDecision Fresh(
        ArchitectureDecision decision, Spec? spec)
    {
        var current = spec is not null && spec.Status == "Approved" &&
            spec.Version == decision.SpecificationVersion &&
            spec.Hash == decision.SpecificationSha256;
        return decision with
        {
            SpecificationCurrent = current,
            EffectiveStatus = current ? decision.Status : "STALE_SPECIFICATION"
        };
    }

    private static void EnsureTask(SqliteConnection db,
        SqliteTransaction? tx, string id)
    {
        ValidateTaskId(id);
        if (TaskStore.FindTask(db, tx, id) is null)
            throw new StoreFailure("NOT_FOUND");
    }

    public IReadOnlyList<ArchitectureDecision> List(string taskId)
    {
        using var db = tasks.Open();
        EnsureTask(db, null, taskId);
        var decisions = new List<ArchitectureDecision>();
        using (var cmd = TaskStore.Command(db, null, SelectColumns + """
             WHERE task_id=@task AND revision=(
               SELECT MAX(revision) FROM architecture_decisions AS other
               WHERE other.task_id=architecture_decisions.task_id
                 AND other.topic=architecture_decisions.topic)
             ORDER BY topic
            """, ("@task", taskId)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                decisions.Add(ReadRow(reader));
        var spec = LatestSpec(db, null, taskId);
        return decisions.Select(item => Fresh(item, spec)).ToArray();
    }

    public IReadOnlyList<ArchitectureDecision> History(string taskId, string topic)
    {
        topic = ValidId(topic);
        using var db = tasks.Open();
        EnsureTask(db, null, taskId);
        var decisions = new List<ArchitectureDecision>();
        using (var cmd = TaskStore.Command(db, null, SelectColumns + """
             WHERE task_id=@task AND topic=@topic ORDER BY revision
            """, ("@task", taskId), ("@topic", topic)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                decisions.Add(ReadRow(reader));
        var spec = LatestSpec(db, null, taskId);
        return decisions.Select(item => Fresh(item, spec)).ToArray();
    }

    public ArchitectureDecision Propose(string taskId,
        ProposeArchitectureDecisionRequest input)
    {
        ValidateTaskId(taskId);
        if (input.SchemaVersion != SchemaVersion || input.ExpectedRevision < 0)
            throw new StoreFailure("INVALID_INPUT");
        var topic = ValidId(input.Topic);
        var recommended = ValidId(input.RecommendedOptionId);
        var rationale = ValidText(input.Rationale, 2000);
        if (input.Options is null || input.Options.Count is < 2 or > 6)
            throw new StoreFailure("INVALID_INPUT");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var options = new List<ArchitectureOption>();
        foreach (var option in input.Options)
        {
            if (option is null)
                throw new StoreFailure("INVALID_INPUT");
            var id = ValidId(option.Id);
            if (!ids.Add(id))
                throw new StoreFailure("INVALID_INPUT");
            var profile = option.RuntimeProfileId;
            if (profile is not null &&
                RuntimeProfileRegistry.Find(profile) is null)
                throw new StoreFailure("INVALID_PROFILE");
            options.Add(new ArchitectureOption(id,
                ValidText(option.Label, 100),
                ValidText(option.Benefit, 500),
                ValidText(option.Tradeoff, 500), profile));
        }
        if (!ids.Contains(recommended))
            throw new StoreFailure("INVALID_INPUT");

        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        EnsureTask(db, tx, taskId);
        _ = RequireApprovedSpec(db, tx, taskId,
            input.SpecificationVersion, input.SpecificationSha256);
        var latest = Latest(db, tx, taskId, topic);
        if ((latest?.Revision ?? 0) != input.ExpectedRevision)
            throw new StoreFailure("STALE_DECISION");
        var revision = input.ExpectedRevision + 1;
        var json = JsonSerializer.Serialize(options);
        var canonical = JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion, taskId, topic, revision,
            specificationVersion = input.SpecificationVersion,
            specificationSha256 = input.SpecificationSha256,
            recommendedOptionId = recommended, rationale, options
        });
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var utc = DateTimeOffset.UtcNow.ToString("O");

        TaskStore.Execute(db, tx, """
            UPDATE architecture_decisions SET status='Superseded'
            WHERE task_id=@task AND topic=@topic
              AND revision=@previous AND status IN ('Proposed','Approved','Deferred')
            """, ("@task", taskId), ("@topic", topic),
            ("@previous", input.ExpectedRevision));
        TaskStore.Execute(db, tx, """
            INSERT INTO architecture_decisions(
                task_id,topic,revision,schema_version,specification_version,
                specification_sha256,recommended_option_id,rationale,
                options_json,proposal_sha256,status,proposed_utc)
            VALUES(@task,@topic,@revision,@schema,@specVersion,@specHash,
                @recommended,@rationale,@options,@hash,'Proposed',@utc)
            """, ("@task", taskId), ("@topic", topic),
            ("@revision", revision), ("@schema", SchemaVersion),
            ("@specVersion", input.SpecificationVersion),
            ("@specHash", input.SpecificationSha256!),
            ("@recommended", recommended), ("@rationale", rationale),
            ("@options", json), ("@hash", hash), ("@utc", utc));
        tx.Commit();
        return new ArchitectureDecision(SchemaVersion, taskId, topic,
            revision, input.SpecificationVersion, input.SpecificationSha256!,
            recommended, rationale, options.AsReadOnly(), hash, "Proposed",
            null, utc, null, true, "Proposed");
    }

    public ArchitectureDecision Review(string taskId, string topic,
        ReviewArchitectureDecisionRequest input)
    {
        ValidateTaskId(taskId);
        topic = ValidId(topic);
        if (input.SchemaVersion != SchemaVersion ||
            input.ExpectedRevision < 1 ||
            input.ProposalSha256 is null ||
            !HashPattern.IsMatch(input.ProposalSha256))
            throw new StoreFailure("INVALID_INPUT");
        if (input.Action is not ("Approve" or "Defer"))
            throw new StoreFailure("INVALID_INPUT");
        var status = input.Action == "Approve" ? "Approved" : "Deferred";
        var confirmation = (input.Action == "Approve" ? "APPROVE " : "DEFER ") +
            input.ProposalSha256;
        if (!string.Equals(confirmation, input.Confirmation,
                StringComparison.Ordinal))
            throw new StoreFailure("APPROVAL_MISMATCH");

        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        EnsureTask(db, tx, taskId);
        var latest = Latest(db, tx, taskId, topic) ??
            throw new StoreFailure("NOT_FOUND");
        if (latest.Revision != input.ExpectedRevision ||
            latest.ProposalSha256 != input.ProposalSha256 ||
            latest.Status != "Proposed")
            throw new StoreFailure("STALE_DECISION");
        _ = RequireApprovedSpec(db, tx, taskId,
            latest.SpecificationVersion, latest.SpecificationSha256);
        var selected = input.Action == "Approve"
            ? ValidId(input.SelectedOptionId)
            : null;
        if (input.Action == "Defer" && input.SelectedOptionId is not null ||
            selected is not null && !latest.Options.Any(o => o.Id == selected))
            throw new StoreFailure("INVALID_INPUT");

        var utc = DateTimeOffset.UtcNow.ToString("O");
        if (TaskStore.Execute(db, tx, """
            UPDATE architecture_decisions
            SET status=@status,selected_option_id=@selected,reviewed_utc=@utc
            WHERE task_id=@task AND topic=@topic AND revision=@revision
              AND proposal_sha256=@hash AND status='Proposed'
            """, ("@status", status), ("@selected", (object?)selected ?? DBNull.Value),
            ("@utc", utc), ("@task", taskId), ("@topic", topic),
            ("@revision", latest.Revision),
            ("@hash", latest.ProposalSha256)) != 1)
            throw new StoreFailure("STALE_DECISION");
        tx.Commit();
        return latest with
        {
            Status = status, SelectedOptionId = selected,
            ReviewedUtc = utc, SpecificationCurrent = true,
            EffectiveStatus = status
        };
    }
}
