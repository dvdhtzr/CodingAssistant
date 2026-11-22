using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record CreateRepairProposalRequest(string? QaReviewId);
public sealed record RepairFindingReference(
    string Path, int Index, string Severity, string Requirement,
    string Evidence, string Recommendation, string Classification);
public sealed record RepairProposal(
    string Id, string TaskId, string QaReviewId, string EngineeringRunId,
    string SourceSha256, int PlanRevision, string PlanSha256,
    int SpecificationVersion, string SpecificationSha256,
    string ArchitectureSha256, string State,
    IReadOnlyList<RepairFindingReference> Findings, string CreatedUtc);

/// <summary>
/// R1: persist an immutable QA-bound triage proposal. This is never an
/// execution grant. Findings are unverified hypotheses until independently
/// classified against approved requirements and evidence.
/// </summary>
public sealed class RepairProposalStore(TaskStore tasks)
{
    private static readonly Regex Id = new("^[0-9a-f]{32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS repair_proposals (
                id TEXT PRIMARY KEY NOT NULL,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                qa_review_id TEXT NOT NULL UNIQUE REFERENCES qa_reviews(id),
                engineering_run_id TEXT NOT NULL REFERENCES engineering_runs(id),
                source_sha256 TEXT NOT NULL,
                plan_revision INTEGER NOT NULL,
                plan_sha256 TEXT NOT NULL,
                spec_version INTEGER NOT NULL,
                spec_sha256 TEXT NOT NULL,
                architecture_sha256 TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state='NeedsTriage'),
                findings_json TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS repair_proposal_by_task
                ON repair_proposals(task_id,created_utc DESC,id DESC);
            """);
    }

    private static RepairProposal Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.GetString(4), r.GetInt32(5), r.GetString(6), r.GetInt32(7),
        r.GetString(8), r.GetString(9), r.GetString(10),
        JsonSerializer.Deserialize<RepairFindingReference[]>(r.GetString(11))
            ?? throw new StoreFailure("INVALID_STATE"),
        r.GetString(12));

    private const string Columns = """
        SELECT id,task_id,qa_review_id,engineering_run_id,source_sha256,
               plan_revision,plan_sha256,spec_version,spec_sha256,
               architecture_sha256,state,findings_json,created_utc
        FROM repair_proposals
        """;

    public RepairProposal? Latest(string taskId)
    {
        ValidateId(taskId);
        using var db = tasks.Open();
        _ = TaskStore.FindTask(db, null, taskId) ??
            throw new StoreFailure("NOT_FOUND");
        using var cmd = TaskStore.Command(db, null, Columns +
            " WHERE task_id=@task ORDER BY created_utc DESC,id DESC LIMIT 1",
            ("@task", taskId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public RepairProposal Propose(string taskId, CreateRepairProposalRequest request)
    {
        ValidateId(taskId);
        ValidateId(request.QaReviewId);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        _ = TaskStore.FindTask(db, tx, taskId) ??
            throw new StoreFailure("NOT_FOUND");

        QaReviewRun review;
        using (var command = TaskStore.Command(db, tx, """
            SELECT id,task_id,engineering_run_id,source_sha256,plan_revision,
                   plan_sha256,spec_version,spec_sha256,architecture_sha256,
                   model,state,phase,error_code,created_utc,updated_utc
            FROM qa_reviews WHERE id=@qa AND task_id=@task
            """, ("@qa", request.QaReviewId!), ("@task", taskId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                throw new StoreFailure("NOT_FOUND");
            review = new QaReviewRun(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
                reader.GetString(5), reader.GetInt32(6), reader.GetString(7),
                reader.GetString(8), reader.GetString(9), reader.GetString(10),
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetString(13), reader.GetString(14));
        }
        if (review.State != "Reviewed")
            throw new StoreFailure("INVALID_STATE");
        var latestQa = TaskStore.Scalar(db, tx, """
            SELECT id FROM qa_reviews WHERE task_id=@task
            ORDER BY created_utc DESC,id DESC LIMIT 1
            """, ("@task", taskId));
        var latestSource = TaskStore.Scalar(db, tx, """
            SELECT id FROM engineering_runs WHERE task_id=@task
            ORDER BY created_utc DESC,id DESC LIMIT 1
            """, ("@task", taskId));
        if (latestQa != review.Id || latestSource != review.EngineeringRunId)
            throw new StoreFailure("SOURCE_CHANGED");

        EngineeringRun source;
        using (var command = TaskStore.Command(db, tx, """
            SELECT id,task_id,plan_revision,plan_sha256,spec_version,spec_sha256,
                   architecture_sha256,model,state,phase,error_code,created_utc,
                   updated_utc FROM engineering_runs WHERE id=@id
            """, ("@id", review.EngineeringRunId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new StoreFailure("SOURCE_CHANGED");
            source = new EngineeringRun(reader.GetString(0), reader.GetString(1),
                reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetString(11), reader.GetString(12));
        }
        if (!QaReviewStore.IsReviewableSource(source.State, 1) ||
            source.PlanRevision != review.PlanRevision ||
            source.PlanSha256 != review.PlanSha256 ||
            source.SpecificationVersion != review.SpecificationVersion ||
            source.SpecificationSha256 != review.SpecificationSha256 ||
            source.ArchitectureSha256 != review.ArchitectureSha256 ||
            source.Model != review.Model)
            throw new StoreFailure("SOURCE_CHANGED");
        EngineeringRunStore.RequireBinding(db, tx, source);

        var files = new List<EngineeringSourceFile>();
        using (var command = TaskStore.Command(db, tx, """
            SELECT path,purpose,content,sha256,work_item_id,updated_utc
            FROM engineering_run_files WHERE run_id=@id ORDER BY path
            """, ("@id", source.Id)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                files.Add(new EngineeringSourceFile(reader.GetString(0),
                    reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5)));
        if (!QaReviewStore.IsReviewableSource(source.State, files.Count) ||
            QaReviewStore.Fingerprint(files) != review.SourceSha256)
            throw new StoreFailure("SOURCE_CHANGED");

        var findings = new List<RepairFindingReference>();
        var reviewed = 0;
        using (var command = TaskStore.Command(db, tx, """
            SELECT path,source_sha256,assessment_json FROM qa_review_files
            WHERE qa_id=@qa ORDER BY path
            """, ("@qa", review.Id)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var path = reader.GetString(0);
                if (!files.Any(f => string.Equals(f.Path, path,
                        StringComparison.OrdinalIgnoreCase) &&
                        f.Sha256 == reader.GetString(1)))
                    throw new StoreFailure("SOURCE_CHANGED");
                var report = JsonSerializer.Deserialize<QaFileAssessment>(
                    reader.GetString(2)) ?? throw new StoreFailure("INVALID_STATE");
                report = MafQaEngineer.Parse(JsonSerializer.Serialize(report));
                for (var i = 0; i < report.Findings.Count; i++)
                {
                    var f = report.Findings[i];
                    findings.Add(new RepairFindingReference(path, i,
                        f.Severity, f.Requirement, f.Evidence, f.Recommendation,
                        "NeedsVerification"));
                }
                reviewed++;
            }
        if (reviewed != files.Count || findings.Count == 0)
            throw new StoreFailure("INVALID_STATE");

        using (var previous = TaskStore.Command(db, tx, Columns +
            " WHERE task_id=@task AND qa_review_id=@qa",
            ("@task", taskId), ("@qa", request.QaReviewId!)))
        using (var reader = previous.ExecuteReader())
        {
            if (reader.Read())
            {
                var existing = Read(reader);
                return existing;
            }
        }


        // Check for another writer's previous proposal under SQLite's write lock.
        var now = DateTimeOffset.UtcNow.ToString("O");
        var proposal = new RepairProposal(Guid.NewGuid().ToString("N"),
            taskId, review.Id, source.Id, review.SourceSha256,
            review.PlanRevision, review.PlanSha256,
            review.SpecificationVersion, review.SpecificationSha256,
            review.ArchitectureSha256, "NeedsTriage", findings.AsReadOnly(), now);
        try
        {
            TaskStore.Execute(db, tx, """
                INSERT INTO repair_proposals (
                    id,task_id,qa_review_id,engineering_run_id,source_sha256,
                    plan_revision,plan_sha256,spec_version,spec_sha256,
                    architecture_sha256,state,findings_json,created_utc)
                VALUES (@id,@task,@qa,@run,@source,@revision,@plan,@sv,@spec,
                        @arch,'NeedsTriage',@findings,@now)
                """, ("@id", proposal.Id), ("@task", taskId),
                ("@qa", review.Id), ("@run", source.Id),
                ("@source", review.SourceSha256),
                ("@revision", review.PlanRevision), ("@plan", review.PlanSha256),
                ("@sv", review.SpecificationVersion),
                ("@spec", review.SpecificationSha256),
                ("@arch", review.ArchitectureSha256),
                ("@findings", JsonSerializer.Serialize(findings)), ("@now", now));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new StoreFailure("BUILD_BUSY");
        }
        tx.Commit();
        return proposal;
    }

    private static void ValidateId(string? value)
    {
        if (value is null || !Id.IsMatch(value))
            throw new StoreFailure("INVALID_ID");
    }
}
