using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

public sealed record MafHandoffEvent(
    string TaskId, string Kind, string FromAgent, string ToAgent,
    int SpecificationVersion, string SpecificationSha256,
    int Revision, string Model, string CreatedUtc, bool Current,
    string? PlanSha256 = null, string? ArchitectureSha256 = null,
    string? WorkItemId = null);

/// <summary>
/// Owner-private, append-only evidence of host-authorized agent handoffs.
/// Approval remains an InterviewStore transaction; MAF has no permission
/// to approve or bypass it. The handoff references the immutable approved
/// specification hash, never a model assertion of approval.
/// </summary>
public sealed class MafHandoffStore(TaskStore tasks, InterviewStore interviews)
{
    private static readonly Regex Id = new(
        "^[0-9a-f]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS maf_handoffs (
                task_id TEXT NOT NULL REFERENCES tasks(id),
                kind TEXT NOT NULL CHECK(kind IN
                    ('SpecificationHandoff','ArchitectureProposalSaved')),
                topic TEXT NOT NULL,
                from_agent TEXT NOT NULL,
                to_agent TEXT NOT NULL,
                specification_version INTEGER NOT NULL,
                specification_sha256 TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK(revision >= 0),
                model TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(task_id,kind,topic,specification_version,revision)
            );
            """);
    }

    public void RecordApproved(string taskId, InterviewSpecification spec)
    {
        Record(taskId, "SpecificationHandoff", "", "ProductOwner",
            "SolutionArchitect", spec, 0);
    }

    public void RecordProposal(string taskId, string topic,
        InterviewSpecification spec, int revision)
    {
        if (topic is not ("technology-stack" or "architecture-style") || revision < 1)
            throw new StoreFailure("INVALID_INPUT");
        Record(taskId, "ArchitectureProposalSaved", topic,
            "SolutionArchitect", "HumanOwner", spec, revision);
    }

    private void Record(string taskId, string kind, string topic,
        string from, string to, InterviewSpecification spec, int revision)
    {
        if (!Id.IsMatch(taskId) || spec.Status != "Approved" ||
            spec.Sha256.Length != 64 || !LocalInterviewModel.AllowedModel(spec.Model))
            throw new StoreFailure("INVALID_INPUT");
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        // No handoff can be created against a stale or revoked spec.
        var current = TaskStore.Scalar(db, tx, """
            SELECT sha256 FROM interview_specs
            WHERE task_id=@id AND version=@version AND status='Approved'
            """, ("@id", taskId), ("@version", spec.Version));
        if (current != spec.Sha256)
            throw new StoreFailure("SPECIFICATION_CONFLICT");
        TaskStore.Execute(db, tx, """
            INSERT OR IGNORE INTO maf_handoffs (
                task_id,kind,topic,from_agent,to_agent,specification_version,
                specification_sha256,revision,model,created_utc)
            VALUES (@id,@kind,@topic,@from,@to,@version,@sha,@revision,@model,@utc)
            """, ("@id", taskId), ("@kind", kind), ("@topic", topic),
            ("@from", from), ("@to", to), ("@version", spec.Version),
            ("@sha", spec.Sha256), ("@revision", revision),
            ("@model", spec.Model),
            ("@utc", DateTimeOffset.UtcNow.ToString("O")));
        tx.Commit();
    }

    public IReadOnlyList<MafHandoffEvent> List(string taskId)
    {
        if (!Id.IsMatch(taskId))
            throw new StoreFailure("INVALID_ID");
        var current = interviews.Read(taskId).Specification;
        using var db = tasks.Open();
        using var command = TaskStore.Command(db, null, """
            SELECT task_id,kind,from_agent,to_agent,specification_version,
                specification_sha256,revision,model,created_utc
            FROM maf_handoffs WHERE task_id=@id
            ORDER BY created_utc,kind,topic,revision
            """, ("@id", taskId));
        using var reader = command.ExecuteReader();
        var list = new List<MafHandoffEvent>();
        while (reader.Read())
        {
            var version = reader.GetInt32(4);
            var sha = reader.GetString(5);
            list.Add(new MafHandoffEvent(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), version, sha, reader.GetInt32(6),
                reader.GetString(7), reader.GetString(8),
                current?.Status == "Approved" &&
                current.Version == version && current.Sha256 == sha));
        }
        return list.AsReadOnly();
    }
}
