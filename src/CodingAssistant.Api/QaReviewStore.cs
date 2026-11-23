using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record StartQaReviewRequest(string? EngineeringRunId, string? PlanSha256);
public sealed record QaReviewRun(
    string Id, string TaskId, string EngineeringRunId,
    string SourceSha256, int PlanRevision, string PlanSha256,
    int SpecificationVersion, string SpecificationSha256,
    string ArchitectureSha256, string Model,
    string State, string Phase, string? ErrorCode,
    string CreatedUtc, string UpdatedUtc);
public sealed record QaReviewEvent(
    int Sequence, string Kind, string Detail, string CreatedUtc);
public sealed record QaReviewSnapshot(
    QaReviewRun Job, IReadOnlyList<QaFileReport> Reports,
    IReadOnlyList<QaReviewEvent> Events, string SourceState);

/// <summary>
/// Evidence ledger for *static* QA source inspection. Review admission,
/// each per-file report and completion recheck the current spec, architecture,
/// approved TL plan and immutable engineering source fingerprint inside SQLite.
/// Neither this store nor its MAF model executes or verifies generated code.
/// </summary>
public sealed class QaReviewStore(TaskStore tasks, ImplementationPlanStore plans)
{
    private static readonly Regex Id = new("^[0-9a-f]{32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Failed and interrupted engineer jobs have immutable, inspectable
    // saved files; running jobs do not. QA must never imply source completion.
    public static bool IsReviewableSource(string state, int fileCount) =>
        fileCount > 0 && (state is "Generated" or "Failed" or "Interrupted");
    private static void ValidateId(string? id)
    {
        if (id is null || !Id.IsMatch(id))
            throw new StoreFailure("INVALID_ID");
    }

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS qa_reviews (
                id TEXT PRIMARY KEY NOT NULL,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                engineering_run_id TEXT NOT NULL REFERENCES engineering_runs(id),
                source_sha256 TEXT NOT NULL,
                plan_revision INTEGER NOT NULL,
                plan_sha256 TEXT NOT NULL,
                spec_version INTEGER NOT NULL,
                spec_sha256 TEXT NOT NULL,
                architecture_sha256 TEXT NOT NULL,
                model TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN
                    ('Running','Reviewed','Failed','Interrupted')),
                phase TEXT NOT NULL, error_code TEXT,
                created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS qa_review_one_active
                ON qa_reviews(task_id) WHERE state='Running';
            CREATE INDEX IF NOT EXISTS qa_review_latest
                ON qa_reviews(task_id,created_utc DESC,id DESC);
            CREATE TABLE IF NOT EXISTS qa_review_files (
                qa_id TEXT NOT NULL REFERENCES qa_reviews(id),
                path TEXT NOT NULL COLLATE NOCASE,
                source_sha256 TEXT NOT NULL,
                assessment_json TEXT NOT NULL,
                reviewed_utc TEXT NOT NULL,
                PRIMARY KEY(qa_id,path)
            );
            CREATE TABLE IF NOT EXISTS qa_review_events (
                qa_id TEXT NOT NULL REFERENCES qa_reviews(id),
                sequence INTEGER NOT NULL,
                kind TEXT NOT NULL,
                detail TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(qa_id,sequence)
            );
            """);
    }

    public void MarkInterruptedOnStartup()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            UPDATE qa_reviews SET state='Interrupted',
                phase='RestartInterrupted',error_code='PROCESS_INTERRUPTED',
                updated_utc=@now WHERE state='Running'
            """, ("@now", DateTimeOffset.UtcNow.ToString("O")));
    }

    public static string Fingerprint(IReadOnlyList<EngineeringSourceFile> files)
    {
        if (files.Count is < 1 or > EngineeringRunStore.MaxDistinctFiles)
            throw new StoreFailure("BUILD_NOT_COMPLETE");
        var input = string.Join("\n", files.OrderBy(f => f.Path,
            StringComparer.OrdinalIgnoreCase).Select(file =>
                file.Path + "\t" + file.Sha256));
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static string CurrentFingerprint(
        SqliteConnection db, SqliteTransaction tx, string sourceRun)
    {
        var files = new List<EngineeringSourceFile>();
        using var cmd = TaskStore.Command(db, tx, """
            SELECT path,purpose,content,sha256,work_item_id,updated_utc
            FROM engineering_run_files WHERE run_id=@id ORDER BY path
            """, ("@id", sourceRun));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            files.Add(new EngineeringSourceFile(reader.GetString(0),
                reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return Fingerprint(files);
    }

    private const string Columns = """
        SELECT id,task_id,engineering_run_id,source_sha256,plan_revision,
            plan_sha256,spec_version,spec_sha256,architecture_sha256,model,
            state,phase,error_code,created_utc,updated_utc
        FROM qa_reviews
        """;

    private static QaReviewRun Decode(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.GetInt32(4), r.GetString(5), r.GetInt32(6), r.GetString(7),
        r.GetString(8), r.GetString(9), r.GetString(10), r.GetString(11),
        r.IsDBNull(12) ? null : r.GetString(12), r.GetString(13), r.GetString(14));

    private static QaReviewRun? Find(SqliteConnection db, SqliteTransaction? tx,
        string reviewId)
    {
        using var cmd = TaskStore.Command(db, tx,
            Columns + " WHERE id=@id", ("@id", reviewId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Decode(reader) : null;
    }

    private static EngineeringRun RequireSource(
        SqliteConnection db, SqliteTransaction tx, QaReviewRun job)
    {
        using var cmd = TaskStore.Command(db, tx, """
            SELECT id,task_id,plan_revision,plan_sha256,spec_version,spec_sha256,
                   architecture_sha256,model,state,phase,error_code,created_utc,
                   updated_utc
            FROM engineering_runs WHERE id=@id AND task_id=@task
            """, ("@id", job.EngineeringRunId), ("@task", job.TaskId));
        EngineeringRun source;
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read())
                throw new StoreFailure("NOT_FOUND");
            source = new EngineeringRun(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetInt32(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetString(11), reader.GetString(12));
        }
        if (source.State is not ("Generated" or "Failed" or "Interrupted"))
            throw new StoreFailure("BUILD_NOT_COMPLETE");
        var latestSource = TaskStore.Scalar(db, tx, """
            SELECT id FROM engineering_runs WHERE task_id=@task
            ORDER BY created_utc DESC,id DESC LIMIT 1
            """, ("@task", job.TaskId));
        if (source.Id != latestSource)
            throw new StoreFailure("SOURCE_CHANGED");
        if (source.PlanRevision != job.PlanRevision ||
            source.PlanSha256 != job.PlanSha256 ||
            source.SpecificationVersion != job.SpecificationVersion ||
            source.SpecificationSha256 != job.SpecificationSha256 ||
            source.ArchitectureSha256 != job.ArchitectureSha256 ||
            source.Model != job.Model)
            throw new StoreFailure("STALE_IMPLEMENTATION_PLAN");
        EngineeringRunStore.RequireBinding(db, tx, source);
        if (CurrentFingerprint(db, tx, source.Id) != job.SourceSha256)
            throw new StoreFailure("SOURCE_CHANGED");
        return source;
    }

    private static void Event(SqliteConnection db, SqliteTransaction tx,
        string reviewId, string kind, string detail)
    {
        if (detail.Length is < 1 or > 180 ||
            detail.Any(char.IsControl))
            throw new StoreFailure("INVALID_BUILD_EVENT");
        TaskStore.Execute(db, tx, """
            INSERT INTO qa_review_events(qa_id,sequence,kind,detail,created_utc)
            VALUES(@id, (SELECT COALESCE(MAX(sequence),0)+1
                FROM qa_review_events WHERE qa_id=@id),@kind,@detail,@now)
            """, ("@id", reviewId), ("@kind", kind), ("@detail", detail),
            ("@now", DateTimeOffset.UtcNow.ToString("O")));
    }

    public QaReviewRun Start(string taskId,
        EngineeringRunSnapshot source, ImplementationPlan plan)
    {
        ValidateId(taskId);
        if (source.Job.TaskId != taskId ||
            !IsReviewableSource(source.Job.State, source.Files.Count))
            throw new StoreFailure("BUILD_NOT_COMPLETE");
        if (plan.TaskId != taskId || plan.Status != "Approved" || !plan.Current ||
            source.Job.PlanSha256 != plan.PlanSha256 ||
            source.Job.PlanRevision != plan.Revision)
            throw new StoreFailure("PLAN_NOT_APPROVED");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var review = new QaReviewRun(
            Guid.NewGuid().ToString("N"), taskId, source.Job.Id,
            Fingerprint(source.Files), plan.Revision, plan.PlanSha256,
            plan.SpecificationVersion, plan.SpecificationSha256,
            plan.ArchitectureSha256, plan.Model,
            "Running", "Queued", null, now, now);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        RequireSource(db, tx, review);
        try
        {
            TaskStore.Execute(db, tx, """
                INSERT INTO qa_reviews(
                    id,task_id,engineering_run_id,source_sha256,plan_revision,
                    plan_sha256,spec_version,spec_sha256,architecture_sha256,
                    model,state,phase,created_utc,updated_utc)
                VALUES(@id,@task,@source,@sourceSha,@revision,@planSha,
                    @sv,@ss,@arch,@model,'Running','Queued',@now,@now)
                """, ("@id", review.Id), ("@task", taskId),
                ("@source", review.EngineeringRunId),
                ("@sourceSha", review.SourceSha256),
                ("@revision", review.PlanRevision),
                ("@planSha", review.PlanSha256),
                ("@sv", review.SpecificationVersion),
                ("@ss", review.SpecificationSha256),
                ("@arch", review.ArchitectureSha256),
                ("@model", review.Model), ("@now", now));
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            throw new StoreFailure("BUILD_BUSY");
        }
        Event(db, tx, review.Id, "QaReviewAdmitted",
            source.Job.State == "Generated"
                ? "Independent static QA of generated source; no tests executed"
                : "Independent static QA of PARTIAL " + source.Job.State +
                  " source; incomplete project, no tests executed");
        // This is a real host-verified handoff, not a model-generated claim.
        // Count ONLY recorded inspection events on this exact source run
        // under the same QA admission and source-binding transaction.
        var inspectionText = TaskStore.Scalar(db, tx, """
            SELECT COUNT(*) FROM engineering_run_events
            WHERE run_id=@run AND kind='EngineerAtlasInspected'
            """, ("@run", source.Job.Id));
        if (int.TryParse(inspectionText, out var inspectionCount) &&
            inspectionCount > 0)
            Event(db, tx, review.Id, "QaReceivedEngineerInspectionEvidence",
                "Independent QA received " +
                Math.Min(inspectionCount, 12) +
                " saved source-inspection activity record(s) from this " +
                "engineering run; no build or tests executed");
        tx.Commit();
        return review;
    }

    public QaReviewSnapshot? Latest(string taskId)
    {
        ValidateId(taskId);
        _ = tasks.GetTask(taskId);
        using var db = tasks.Open();
        var id = TaskStore.Scalar(db, null, """
            SELECT id FROM qa_reviews WHERE task_id=@task
            ORDER BY created_utc DESC,id DESC LIMIT 1
            """, ("@task", taskId));
        return id is null ? null : Get(taskId, id);
    }

    public QaReviewSnapshot Get(string taskId, string reviewId)
    {
        ValidateId(taskId);
        ValidateId(reviewId);
        using var db = tasks.Open();
        var review = Find(db, null, reviewId);
        if (review is null || review.TaskId != taskId)
            throw new StoreFailure("NOT_FOUND");
        var reports = new List<QaFileReport>();
        using (var cmd = TaskStore.Command(db, null, """
            SELECT path,source_sha256,assessment_json,reviewed_utc
            FROM qa_review_files WHERE qa_id=@id ORDER BY path
            """, ("@id", reviewId)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                reports.Add(new QaFileReport(reader.GetString(0),
                    reader.GetString(1),
                    JsonSerializer.Deserialize<QaFileAssessment>(
                        reader.GetString(2)) ??
                        throw new InvalidOperationException("Invalid stored QA report"),
                    reader.GetString(3)));
        var events = new List<QaReviewEvent>();
        using (var cmd = TaskStore.Command(db, null, """
            SELECT sequence,kind,detail,created_utc
            FROM qa_review_events WHERE qa_id=@id ORDER BY sequence
            """, ("@id", reviewId)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                events.Add(new QaReviewEvent(reader.GetInt32(0),
                    reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        var sourceState = TaskStore.Scalar(db, null, """
            SELECT state FROM engineering_runs WHERE id=@id AND task_id=@task
            """, ("@id", review.EngineeringRunId), ("@task", taskId));
        return new QaReviewSnapshot(review, reports.AsReadOnly(),
            events.AsReadOnly(), sourceState ?? "Unavailable");
    }

    public void SaveFileReport(string taskId, string reviewId,
        EngineeringSourceFile source, QaFileAssessment assessment)
    {
        ValidateId(taskId);
        ValidateId(reviewId);
        var valid = MafQaEngineer.Parse(JsonSerializer.Serialize(assessment));
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var job = Find(db, tx, reviewId);
        if (job is null || job.TaskId != taskId)
            throw new StoreFailure("NOT_FOUND");
        if (job.State != "Running")
            throw new StoreFailure("INVALID_STATE");
        RequireSource(db, tx, job);
        var sha = TaskStore.Scalar(db, tx, """
            SELECT sha256 FROM engineering_run_files
            WHERE run_id=@run AND path=@path
            """, ("@run", job.EngineeringRunId), ("@path", source.Path));
        if (sha != source.Sha256)
            throw new StoreFailure("SOURCE_CHANGED");
        var now = DateTimeOffset.UtcNow.ToString("O");
        try
        {
            TaskStore.Execute(db, tx, """
                INSERT INTO qa_review_files(
                    qa_id,path,source_sha256,assessment_json,reviewed_utc)
                VALUES(@id,@path,@sha,@json,@now)
                """, ("@id", reviewId), ("@path", source.Path),
                ("@sha", source.Sha256),
                ("@json", JsonSerializer.Serialize(valid)), ("@now", now));
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            throw new StoreFailure("INVALID_STATE");
        }
        TaskStore.Execute(db, tx, """
            UPDATE qa_reviews SET phase=@phase,updated_utc=@now
            WHERE id=@id
            """, ("@id", reviewId),
            ("@phase", "Reviewed:" + source.Path), ("@now", now));
        Event(db, tx, reviewId, "QaFileReviewed",
            source.Path + " static review saved (" +
            valid.Findings.Count + " possible issue(s); not tested)");
        tx.Commit();
    }

    /// <summary>
    /// Save only host-issued, one-file QA inspection metadata. The source and
    /// approved plan are rechecked in the same transaction as the event; QA
    /// remains read-only and no inspection can certify a build or test result.
    /// </summary>
    public void RecordAtlasInspection(string taskId, string reviewId,
        EngineeringSourceFile file, AtlasInspectionObservation observation)
    {
        ValidateId(taskId); ValidateId(reviewId);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Name is not ("atlas.find_symbols" or
            "atlas.get_relationships" or "atlas.source_excerpt") ||
            observation.ResultCount is < 0 or > 16 ||
            observation.SourceRevision is null ||
            observation.SourceRevision.Length != 64 ||
            !observation.SourceRevision.All(ch =>
                ch is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new StoreFailure("INVALID_INPUT");

        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var job = Find(db, tx, reviewId);
        if (job is null || job.TaskId != taskId)
            throw new StoreFailure("NOT_FOUND");
        if (job.State != "Running")
            throw new StoreFailure("INVALID_STATE");
        RequireSource(db, tx, job);
        var sha = TaskStore.Scalar(db, tx, """
            SELECT sha256 FROM engineering_run_files
            WHERE run_id=@run AND path=@path
            """, ("@run", job.EngineeringRunId), ("@path", file.Path));
        if (sha is null || sha != file.Sha256)
            throw new StoreFailure("SOURCE_CHANGED");

        // The tool's revision is independently recomputed from the actual
        // saved files, not accepted from the model or a caller's narration.
        var savedFiles = new List<EngineeringSourceFile>();
        using (var cmd = TaskStore.Command(db, tx, """
            SELECT path,purpose,content,sha256,work_item_id,updated_utc
            FROM engineering_run_files WHERE run_id=@run ORDER BY path
            """, ("@run", job.EngineeringRunId)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                savedFiles.Add(new EngineeringSourceFile(
                    reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5)));
        if (CodeAtlasIndexer.Index(job.EngineeringRunId, savedFiles)
            .SourceRevision != observation.SourceRevision)
            throw new StoreFailure("SOURCE_CHANGED");

        var fileKey = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(file.Path + "\n" + file.Sha256)))
            .ToLowerInvariant();
        var already = TaskStore.Scalar(db, tx, """
            SELECT COUNT(*) FROM qa_review_events
            WHERE qa_id=@id AND kind='QaAtlasInspected'
                AND detail LIKE @fileKey
            """, ("@id", reviewId), ("@fileKey", "% " + fileKey + " %"));
        if (int.TryParse(already, out var count) &&
            count >= AgentAtlasInspectionGateway.MaximumRequestsPerTask)
            throw new StoreFailure("ATLAS_TOOL_LIMIT");

        Event(db, tx, reviewId, "QaAtlasInspected",
            "QaEngineer used " + observation.Name + " file-key " +
            fileKey + " source " + observation.SourceRevision[..12] +
            " (" + observation.ResultCount + " static result(s); not tested)");
        tx.Commit();
    }

    public void Finish(string taskId, string reviewId, bool success, string? code)
    {
        ValidateId(taskId); ValidateId(reviewId);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var job = Find(db, tx, reviewId);
        if (job is null || job.TaskId != taskId)
            throw new StoreFailure("NOT_FOUND");
        if (job.State != "Running")
            return;
        if (success)
        {
            RequireSource(db, tx, job);
            var expected = TaskStore.Scalar(db, tx, """
                SELECT COUNT(*) FROM engineering_run_files WHERE run_id=@run
                """, ("@run", job.EngineeringRunId));
            var actual = TaskStore.Scalar(db, tx, """
                SELECT COUNT(*) FROM qa_review_files WHERE qa_id=@id
                """, ("@id", reviewId));
            if (expected != actual || actual == "0")
                throw new StoreFailure("INVALID_STATE");
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        TaskStore.Execute(db, tx, """
            UPDATE qa_reviews SET state=@state,phase=@phase,
                error_code=@error,updated_utc=@now WHERE id=@id
            """, ("@id", reviewId),
            ("@state", success ? "Reviewed" : "Failed"),
            ("@phase", success ? "StaticReviewOnly" : "Failed"),
            ("@error", (object?)code ?? DBNull.Value), ("@now", now));
        var sourceState = TaskStore.Scalar(db, tx, """
            SELECT state FROM engineering_runs WHERE id=@id
            """, ("@id", job.EngineeringRunId));
        Event(db, tx, reviewId,
            success ? "QaReviewCompleted" : "QaReviewFailed",
            success
                ? sourceState == "Generated"
                    ? "All generated files statically reviewed; no tests executed"
                    : "Saved PARTIAL source statically reviewed; project incomplete, no tests executed"
                : "Independent source review stopped safely: " +
                    (code ?? "REVIEW_FAILED"));
        tx.Commit();
    }

    public void Interrupt(string taskId, string reviewId)
    {
        ValidateId(taskId); ValidateId(reviewId);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var job = Find(db, tx, reviewId);
        if (job is null || job.TaskId != taskId)
            throw new StoreFailure("NOT_FOUND");
        if (job.State == "Running")
        {
            TaskStore.Execute(db, tx, """
                UPDATE qa_reviews SET state='Interrupted',phase='Interrupted',
                    error_code='OWNER_CANCELLED',updated_utc=@now WHERE id=@id
                """, ("@id", reviewId),
                ("@now", DateTimeOffset.UtcNow.ToString("O")));
            Event(db, tx, reviewId, "QaReviewInterrupted",
                "Owner stopped independent static source review");
        }
        tx.Commit();
    }

    public IReadOnlyList<MafHandoffEvent> Handoffs(string taskId)
    {
        ValidateId(taskId);
        var current = plans.Read(taskId).Plan;
        using var db = tasks.Open();
        var latestSource = TaskStore.Scalar(db, null, """
            SELECT id FROM engineering_runs WHERE task_id=@task
            ORDER BY created_utc DESC,id DESC LIMIT 1
            """, ("@task", taskId));
        var events = new List<MafHandoffEvent>();
        using var cmd = TaskStore.Command(db, null, """
            SELECT plan_revision,plan_sha256,spec_version,spec_sha256,
                architecture_sha256,model,created_utc,updated_utc,state,
                engineering_run_id
            FROM qa_reviews WHERE task_id=@task ORDER BY created_utc
            """, ("@task", taskId));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var sha = reader.GetString(1);
            var bound = current?.Current == true &&
                current.Status == "Approved" && current.PlanSha256 == sha &&
                reader.GetString(9) == latestSource;
            var source = new MafHandoffEvent(taskId, "QaReviewRequested",
                "HumanOwner", "QaEngineer", reader.GetInt32(2),
                reader.GetString(3), reader.GetInt32(0),
                reader.GetString(5), reader.GetString(6), bound,
                sha, reader.GetString(4));
            events.Add(source);
            if (reader.GetString(8) == "Reviewed")
                events.Add(source with {
                    Kind = "QaStaticReportSaved", FromAgent = "QaEngineer",
                    ToAgent = "HumanOwner", CreatedUtc = reader.GetString(7)
                });
        }
        return events.AsReadOnly();
    }
}
