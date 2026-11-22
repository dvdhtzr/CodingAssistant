using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record ClassifyRepairFindingRequest(
    string? Path, int Index, string? Classification, string? Rationale);
public sealed record RepairTriageDecision(
    string ProposalId, string Path, int Index, string Classification,
    string Rationale, string CreatedUtc);

/// <summary>
/// Host-controlled, append-only owner triage. Classification is a decision
/// record, never approval to run an agent, modify source, or execute tests.
/// </summary>
public sealed class RepairTriageStore(TaskStore tasks)
{
    private static readonly Regex Id = new("^[0-9a-f]{32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    {
        "ImplementationDefect", "NeedsVerification", "SpecificationAmbiguity",
        "PlanChangeRequired", "NonBlockingImprovement"
    };

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS repair_triage_decisions (
                proposal_id TEXT NOT NULL REFERENCES repair_proposals(id),
                path TEXT NOT NULL COLLATE NOCASE,
                finding_index INTEGER NOT NULL CHECK(finding_index >= 0),
                classification TEXT NOT NULL CHECK(classification IN
                    ('ImplementationDefect','NeedsVerification',
                     'SpecificationAmbiguity','PlanChangeRequired',
                     'NonBlockingImprovement')),
                rationale TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(proposal_id,path,finding_index)
            );
            """);
    }

    private static RepairTriageDecision Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3),
        r.GetString(4), r.GetString(5));

    public IReadOnlyList<RepairTriageDecision> List(string taskId, string proposalId)
    {
        ValidateId(taskId);
        ValidateId(proposalId);
        using var db = tasks.Open();
        if (TaskStore.Scalar(db, null, """
            SELECT id FROM repair_proposals
            WHERE id=@id AND task_id=@task
            """, ("@id", proposalId), ("@task", taskId)) is null)
            throw new StoreFailure("NOT_FOUND");
        var result = new List<RepairTriageDecision>();
        using var cmd = TaskStore.Command(db, null, """
            SELECT proposal_id,path,finding_index,classification,rationale,created_utc
            FROM repair_triage_decisions WHERE proposal_id=@id
            ORDER BY path,finding_index
            """, ("@id", proposalId));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(Read(reader));
        return result.AsReadOnly();
    }

    public RepairTriageDecision Classify(string taskId, string proposalId,
        ClassifyRepairFindingRequest request)
    {
        ValidateId(taskId);
        ValidateId(proposalId);
        if (request.Path is null || request.Path.Length is < 1 or > 240 ||
            request.Path.Any(char.IsControl) || request.Index < 0 ||
            request.Classification is null || !Categories.Contains(request.Classification) ||
            request.Rationale is null || request.Rationale.Trim().Length is < 12 or > 500 ||
            request.Rationale.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\t')))
            throw new StoreFailure("INVALID_INPUT");
        var rationale = request.Rationale.Trim();

        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        string runId, qaId, sourceSha, json;
        using (var cmd = TaskStore.Command(db, tx, """
            SELECT engineering_run_id,qa_review_id,source_sha256,findings_json
            FROM repair_proposals WHERE id=@id AND task_id=@task
            """, ("@id", proposalId), ("@task", taskId)))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) throw new StoreFailure("NOT_FOUND");
            runId = reader.GetString(0);
            qaId = reader.GetString(1);
            sourceSha = reader.GetString(2);
            json = reader.GetString(3);
        }
        var findings = JsonSerializer.Deserialize<RepairFindingReference[]>(json)
            ?? throw new StoreFailure("INVALID_STATE");
        if (!findings.Any(f => f.Index == request.Index &&
            string.Equals(f.Path, request.Path, StringComparison.OrdinalIgnoreCase)))
            throw new StoreFailure("INVALID_INPUT");
        if (TaskStore.Scalar(db, tx, """
                SELECT id FROM engineering_runs WHERE task_id=@task
                ORDER BY created_utc DESC,id DESC LIMIT 1
                """, ("@task", taskId)) != runId ||
            TaskStore.Scalar(db, tx, """
                SELECT id FROM qa_reviews WHERE task_id=@task
                ORDER BY created_utc DESC,id DESC LIMIT 1
                """, ("@task", taskId)) != qaId)
            throw new StoreFailure("SOURCE_CHANGED");
        EngineeringRun source;
        using (var binding = TaskStore.Command(db, tx, """
            SELECT id,task_id,plan_revision,plan_sha256,spec_version,spec_sha256,
                architecture_sha256,model,state,phase,error_code,created_utc,
                updated_utc FROM engineering_runs WHERE id=@run
            """, ("@run", runId)))
        using (var reader = binding.ExecuteReader())
        {
            if (!reader.Read()) throw new StoreFailure("SOURCE_CHANGED");
            source = new EngineeringRun(reader.GetString(0), reader.GetString(1),
                reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetString(11), reader.GetString(12));
        }
        EngineeringRunStore.RequireBinding(db, tx, source);

        // Bind triage to the full immutable source, not just a model's path.
        var files = new List<EngineeringSourceFile>();
        using (var cmd = TaskStore.Command(db, tx, """
            SELECT path,purpose,content,sha256,work_item_id,updated_utc
            FROM engineering_run_files WHERE run_id=@run
            """, ("@run", runId)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                files.Add(new EngineeringSourceFile(reader.GetString(0),
                    reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5)));
        if (QaReviewStore.Fingerprint(files) != sourceSha)
            throw new StoreFailure("SOURCE_CHANGED");

        using (var existing = TaskStore.Command(db, tx, """
            SELECT proposal_id,path,finding_index,classification,rationale,created_utc
            FROM repair_triage_decisions
            WHERE proposal_id=@id AND path=@path AND finding_index=@index
            """, ("@id", proposalId), ("@path", request.Path),
            ("@index", request.Index)))
        using (var reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                var prior = Read(reader);
                if (prior.Classification != request.Classification ||
                    prior.Rationale != rationale)
                    throw new StoreFailure("IDEMPOTENCY_CONFLICT");
                return prior;
            }
        }

        var decision = new RepairTriageDecision(proposalId, request.Path,
            request.Index, request.Classification, rationale,
            DateTimeOffset.UtcNow.ToString("O"));
        try
        {
            TaskStore.Execute(db, tx, """
                INSERT INTO repair_triage_decisions
                    (proposal_id,path,finding_index,classification,rationale,created_utc)
                VALUES (@id,@path,@index,@classification,@rationale,@now)
                """, ("@id", proposalId), ("@path", decision.Path),
                ("@index", decision.Index),
                ("@classification", decision.Classification),
                ("@rationale", decision.Rationale),
                ("@now", decision.CreatedUtc));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new StoreFailure("IDEMPOTENCY_CONFLICT");
        }
        tx.Commit();
        return decision;
    }

    private static void ValidateId(string? id)
    {
        if (id is null || !Id.IsMatch(id))
            throw new StoreFailure("INVALID_ID");
    }
}
