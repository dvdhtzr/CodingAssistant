using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record StartEngineeringRunRequest(int PlanRevision, string? PlanSha256);
public sealed record EngineeringRun(
    string Id, string TaskId, int PlanRevision, string PlanSha256,
    int SpecificationVersion, string SpecificationSha256,
    string ArchitectureSha256, string Model,
    string State, string Phase, string? ErrorCode,
    string CreatedUtc, string UpdatedUtc);
public sealed record EngineeringAssignedTask(
    string Id, int Ordinal, string Title, string State,
    string? Summary, string? StartedUtc, string? CompletedUtc);
public sealed record EngineeringRunEvent(
    int Sequence, string Kind, string Detail, string CreatedUtc);
public sealed record EngineeringRunSnapshot(
    EngineeringRun Job, IReadOnlyList<EngineeringAssignedTask> Tasks,
    IReadOnlyList<EngineeringRunEvent> Events,
    IReadOnlyList<EngineeringSourceFile> Files);

/// <summary>
/// One task at a time, bound to immutable owner-approved Technical Lead plan.
/// SQLite persists each completed assigned task and its complete source file
/// revisions in one transaction. No agent can execute, approve, or bypass gates.
/// </summary>
public sealed class EngineeringRunStore(
    TaskStore tasks, ImplementationPlanStore plans)
{
    private static readonly Regex Id = new("^[0-9a-f]{32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public const int MaxDistinctFiles = 12;
    public const int MaxTotalCharacters = 120000;

    private static void ValidId(string id)
    {
        if (id is null || !Id.IsMatch(id))
            throw new StoreFailure("INVALID_ID");
    }

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS engineering_runs (
                id TEXT PRIMARY KEY NOT NULL,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                plan_revision INTEGER NOT NULL,
                plan_sha256 TEXT NOT NULL,
                spec_version INTEGER NOT NULL,
                spec_sha256 TEXT NOT NULL,
                architecture_sha256 TEXT NOT NULL,
                model TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN
                    ('Running','Generated','Failed','Interrupted')),
                phase TEXT NOT NULL, error_code TEXT,
                created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS engineering_run_active
                ON engineering_runs(task_id) WHERE state='Running';
            CREATE INDEX IF NOT EXISTS engineering_run_recent
                ON engineering_runs(task_id,created_utc DESC,id DESC);
            CREATE TABLE IF NOT EXISTS engineering_run_tasks (
                run_id TEXT NOT NULL REFERENCES engineering_runs(id),
                work_item_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                title TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN
                    ('Pending','Running','Completed')),
                summary TEXT, started_utc TEXT, completed_utc TEXT,
                PRIMARY KEY(run_id,work_item_id),
                UNIQUE(run_id,ordinal)
            );
            CREATE TABLE IF NOT EXISTS engineering_run_events (
                run_id TEXT NOT NULL REFERENCES engineering_runs(id),
                sequence INTEGER NOT NULL CHECK(sequence > 0),
                kind TEXT NOT NULL, detail TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(run_id,sequence)
            );
            CREATE TABLE IF NOT EXISTS engineering_run_files (
                run_id TEXT NOT NULL REFERENCES engineering_runs(id),
                path TEXT NOT NULL COLLATE NOCASE,
                purpose TEXT NOT NULL,
                content TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                work_item_id TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY(run_id,path)
            );
            """);
    }

    public void MarkInterruptedOnStartup()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            UPDATE engineering_runs SET state='Interrupted',
                phase='RestartInterrupted',error_code='PROCESS_INTERRUPTED',
                updated_utc=@now WHERE state='Running'
            """, ("@now", DateTimeOffset.UtcNow.ToString("O")));
    }

    private static EngineeringRun ReadRun(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
        reader.GetString(3), reader.GetInt32(4), reader.GetString(5),
        reader.GetString(6), reader.GetString(7), reader.GetString(8),
        reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.GetString(11), reader.GetString(12));

    private static EngineeringRun? Find(SqliteConnection db, SqliteTransaction? tx,
        string runId)
    {
        using var cmd = TaskStore.Command(db, tx, """
            SELECT id,task_id,plan_revision,plan_sha256,spec_version,spec_sha256,
                architecture_sha256,model,state,phase,error_code,created_utc,
                updated_utc FROM engineering_runs WHERE id=@id
            """, ("@id", runId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    private static void RequireBinding(SqliteConnection db, SqliteTransaction tx,
        EngineeringRun job)
    {
        var plan = TaskStore.Scalar(db, tx, """
            SELECT plan_sha256 FROM implementation_plans
            WHERE task_id=@task AND revision=@revision AND status='Approved'
              AND revision=(SELECT MAX(revision) FROM implementation_plans
                            WHERE task_id=@task)
            """, ("@task", job.TaskId), ("@revision", job.PlanRevision));
        if (plan != job.PlanSha256)
            throw new StoreFailure("STALE_IMPLEMENTATION_PLAN");
        var spec = TaskStore.Scalar(db, tx, """
            SELECT sha256 FROM interview_specs
            WHERE task_id=@task AND version=@version AND status='Approved'
              AND version=(SELECT MAX(version) FROM interview_specs
                           WHERE task_id=@task)
            """, ("@task", job.TaskId), ("@version", job.SpecificationVersion));
        if (spec != job.SpecificationSha256)
            throw new StoreFailure("SPECIFICATION_CONFLICT");
        DraftArchitecturePolicy.AssertCurrent(db, tx, job.TaskId,
            job.SpecificationVersion, job.SpecificationSha256,
            job.ArchitectureSha256);
    }

    private static void Event(SqliteConnection db, SqliteTransaction tx,
        string runId, string kind, string detail)
    {
        if (detail.Length is < 1 or > 180 ||
            detail.Any(ch => char.IsControl(ch)))
            throw new StoreFailure("INVALID_BUILD_EVENT");
        TaskStore.Execute(db, tx, """
            INSERT INTO engineering_run_events(run_id,sequence,kind,detail,created_utc)
            VALUES(@run, (SELECT COALESCE(MAX(sequence),0)+1
                          FROM engineering_run_events WHERE run_id=@run),
                @kind,@detail,@now)
            """, ("@run", runId), ("@kind", kind), ("@detail", detail),
            ("@now", DateTimeOffset.UtcNow.ToString("O")));
    }

    public EngineeringRun Start(string taskId, ImplementationPlan approved)
    {
        ValidId(taskId);
        if (approved.TaskId != taskId || approved.Status != "Approved" ||
            !approved.Current || !LocalInterviewModel.AllowedModel(approved.Model))
            throw new StoreFailure("PLAN_NOT_APPROVED");
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var candidate = new EngineeringRun(id, taskId, approved.Revision,
            approved.PlanSha256, approved.SpecificationVersion,
            approved.SpecificationSha256, approved.ArchitectureSha256,
            approved.Model, "Running", "Queued", null, now, now);
        RequireBinding(db, tx, candidate);
        try
        {
            TaskStore.Execute(db, tx, """
                INSERT INTO engineering_runs(
                    id,task_id,plan_revision,plan_sha256,spec_version,spec_sha256,
                    architecture_sha256,model,state,phase,created_utc,updated_utc)
                VALUES(@id,@task,@revision,@sha,@sv,@ss,@arch,@model,
                       'Running','Queued',@now,@now)
                """, ("@id", id), ("@task", taskId),
                ("@revision", approved.Revision), ("@sha", approved.PlanSha256),
                ("@sv", approved.SpecificationVersion),
                ("@ss", approved.SpecificationSha256),
                ("@arch", approved.ArchitectureSha256),
                ("@model", approved.Model), ("@now", now));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new StoreFailure("BUILD_BUSY");
        }
        var ordinal = 0;
        foreach (var task in approved.Outline.Tasks)
        {
            TaskStore.Execute(db, tx, """
                INSERT INTO engineering_run_tasks(
                    run_id,work_item_id,ordinal,title,state)
                VALUES(@run,@item,@ordinal,@title,'Pending')
                """, ("@run", id), ("@item", task.Id),
                ("@ordinal", ordinal++), ("@title", task.Title));
        }
        Event(db, tx, id, "EngineeringRunAdmitted",
            "Owner-approved plan v" + approved.Revision +
            " assigned to SoftwareEngineer; unverified source only");
        tx.Commit();
        return candidate;
    }

    public EngineeringRunSnapshot? Latest(string taskId)
    {
        ValidId(taskId);
        _ = tasks.GetTask(taskId);
        using var db = tasks.Open();
        var id = TaskStore.Scalar(db, null, """
            SELECT id FROM engineering_runs WHERE task_id=@task
            ORDER BY created_utc DESC,id DESC LIMIT 1
            """, ("@task", taskId));
        return id is null ? null : Get(taskId, id);
    }

    public EngineeringRunSnapshot Get(string taskId, string runId)
    {
        ValidId(taskId);
        ValidId(runId);
        using var db = tasks.Open();
        var job = Find(db, null, runId);
        if (job is null || job.TaskId != taskId)
            throw new StoreFailure("NOT_FOUND");
        var assignments = new List<EngineeringAssignedTask>();
        using (var cmd = TaskStore.Command(db, null, """
            SELECT work_item_id,ordinal,title,state,summary,started_utc,completed_utc
            FROM engineering_run_tasks WHERE run_id=@run ORDER BY ordinal
            """, ("@run", runId)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                assignments.Add(new EngineeringAssignedTask(
                    reader.GetString(0), reader.GetInt32(1),
                    reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        var events = new List<EngineeringRunEvent>();
        using (var cmd = TaskStore.Command(db, null, """
            SELECT sequence,kind,detail,created_utc FROM engineering_run_events
            WHERE run_id=@run ORDER BY sequence
            """, ("@run", runId)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                events.Add(new EngineeringRunEvent(reader.GetInt32(0),
                    reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        var files = new List<EngineeringSourceFile>();
        using (var cmd = TaskStore.Command(db, null, """
            SELECT path,purpose,content,sha256,work_item_id,updated_utc
            FROM engineering_run_files WHERE run_id=@run ORDER BY path
            """, ("@run", runId)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                files.Add(new EngineeringSourceFile(reader.GetString(0),
                    reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5)));
        }
        return new EngineeringRunSnapshot(job, assignments.AsReadOnly(),
            events.AsReadOnly(), files.AsReadOnly());
    }

    public void StartTask(string taskId, string runId, string workItemId)
    {
        ValidId(taskId); ValidId(runId);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var job = Find(db, tx, runId);
        if (job is null || job.TaskId != taskId)
            throw new StoreFailure("NOT_FOUND");
        if (job.State != "Running")
            throw new StoreFailure("INVALID_STATE");
        RequireBinding(db, tx, job);
        var ordinal = TaskStore.Scalar(db, tx, """
            SELECT ordinal FROM engineering_run_tasks
            WHERE run_id=@run AND work_item_id=@item AND state='Pending'
            """, ("@run", runId), ("@item", workItemId));
        if (ordinal is null)
            throw new StoreFailure("INVALID_STATE");
        var incomplete = TaskStore.Scalar(db, tx, """
            SELECT 1 FROM engineering_run_tasks
            WHERE run_id=@run AND ordinal<@ordinal AND state!='Completed' LIMIT 1
            """, ("@run", runId), ("@ordinal", int.Parse(ordinal)));
        if (incomplete is not null)
            throw new StoreFailure("INVALID_STATE");
        var now = DateTimeOffset.UtcNow.ToString("O");
        TaskStore.Execute(db, tx, """
            UPDATE engineering_run_tasks SET state='Running',started_utc=@now
            WHERE run_id=@run AND work_item_id=@item
            """, ("@run", runId), ("@item", workItemId), ("@now", now));
        Event(db, tx, runId, "EngineeringTaskStarted",
            "SoftwareEngineer received approved task " + workItemId);
        tx.Commit();
    }

    public void CompleteTask(string taskId, string runId, string workItemId,
        EngineeringChangeSet changes)
    {
        ValidId(taskId); ValidId(runId);
        var safe = MafSoftwareEngineer.Parse(JsonSerializer.Serialize(changes));
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var job = Find(db, tx, runId);
        if (job is null || job.TaskId != taskId)
            throw new StoreFailure("NOT_FOUND");
        if (job.State != "Running")
            throw new StoreFailure("INVALID_STATE");
        RequireBinding(db, tx, job);
        if (TaskStore.Scalar(db, tx, """
            SELECT 1 FROM engineering_run_tasks
            WHERE run_id=@run AND work_item_id=@item AND state='Running'
            """, ("@run", runId), ("@item", workItemId)) is null)
            throw new StoreFailure("INVALID_STATE");

        var arch = DraftArchitecturePolicy.Capture(db, tx, taskId,
            job.SpecificationVersion, job.SpecificationSha256);
        var existing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = TaskStore.Command(db, tx, """
            SELECT path,LENGTH(content) FROM engineering_run_files WHERE run_id=@run
            """, ("@run", runId)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                existing.Add(reader.GetString(0), reader.GetInt32(1));
        var total = existing.Values.Sum();
        foreach (var file in safe.Files)
        {
            DraftArchitecturePolicy.ValidateSourcePath(arch, file.Path);
            if (existing.TryGetValue(file.Path, out var prior))
                total -= prior;
            existing[file.Path] = file.Content.Length;
            total += file.Content.Length;
        }
        if (existing.Count > MaxDistinctFiles || total > MaxTotalCharacters)
            throw new StoreFailure("PROJECT_SIZE_LIMIT");

        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var file in safe.Files)
        {
            var sha = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(file.Content))).ToLowerInvariant();
            TaskStore.Execute(db, tx, """
                INSERT INTO engineering_run_files(
                    run_id,path,purpose,content,sha256,work_item_id,updated_utc)
                VALUES(@run,@path,@purpose,@content,@sha,@item,@now)
                ON CONFLICT(run_id,path) DO UPDATE SET
                    purpose=excluded.purpose,content=excluded.content,
                    sha256=excluded.sha256,work_item_id=excluded.work_item_id,
                    updated_utc=excluded.updated_utc
                """, ("@run", runId), ("@path", file.Path),
                ("@purpose", file.Purpose), ("@content", file.Content),
                ("@sha", sha), ("@item", workItemId), ("@now", now));
        }
        TaskStore.Execute(db, tx, """
            UPDATE engineering_run_tasks SET state='Completed',
                summary=@summary,completed_utc=@now
            WHERE run_id=@run AND work_item_id=@item
            """, ("@run", runId), ("@item", workItemId),
            ("@summary", safe.Summary), ("@now", now));
        TaskStore.Execute(db, tx, """
            UPDATE engineering_runs SET phase=@phase,updated_utc=@now WHERE id=@run
            """, ("@run", runId), ("@phase", "Saved:" + workItemId),
            ("@now", now));
        Event(db, tx, runId, "EngineeringTaskCompleted",
            workItemId + ": " + safe.Files.Count +
            " complete source file replacement(s) persisted (not tested)");
        tx.Commit();
    }

    public void Finish(string taskId, string runId, bool success, string? reason)
    {
        ValidId(taskId); ValidId(runId);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var job = Find(db, tx, runId);
        if (job is null || job.TaskId != taskId)
            throw new StoreFailure("NOT_FOUND");
        if (job.State != "Running")
            return; // Already cancelled, interrupted or concluded.
        if (success)
        {
            RequireBinding(db, tx, job);
            if (TaskStore.Scalar(db, tx, """
                SELECT 1 FROM engineering_run_tasks
                WHERE run_id=@run AND state!='Completed' LIMIT 1
                """, ("@run", runId)) is not null)
                throw new StoreFailure("INVALID_STATE");
            var paths = new List<PlannedProjectFile>();
            using (var cmd = TaskStore.Command(db, tx, """
                SELECT path,purpose FROM engineering_run_files WHERE run_id=@run
                """, ("@run", runId)))
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                    paths.Add(new PlannedProjectFile(reader.GetString(0),
                        reader.GetString(1)));
            if (paths.Count == 0)
                throw new StoreFailure("INVALID_PROJECT_PLAN");
            DraftArchitecturePolicy.ValidatePlannedFiles(
                DraftArchitecturePolicy.Capture(db, tx, taskId,
                    job.SpecificationVersion, job.SpecificationSha256),
                paths);
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        TaskStore.Execute(db, tx, """
            UPDATE engineering_runs SET state=@state,phase=@phase,
                error_code=@error,updated_utc=@now WHERE id=@run
            """, ("@run", runId), ("@state", success ? "Generated" : "Failed"),
            ("@phase", success ? "SourceOnlyGenerated" : "Failed"),
            ("@error", (object?)reason ?? DBNull.Value), ("@now", now));
        Event(db, tx, runId, success ? "EngineeringRunCompleted" : "EngineeringRunFailed",
            success ? "All planned tasks saved as unverified source, not executed" :
                "Stopped safely: " + (reason ?? "GENERATION_FAILED"));
        tx.Commit();
    }

    public void Interrupt(string taskId, string runId)
    {
        ValidId(taskId); ValidId(runId);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var job = Find(db, tx, runId);
        if (job is null || job.TaskId != taskId)
            throw new StoreFailure("NOT_FOUND");
        if (job.State == "Running")
        {
            TaskStore.Execute(db, tx, """
                UPDATE engineering_runs SET state='Interrupted',
                    phase='Interrupted',error_code='OWNER_CANCELLED',
                    updated_utc=@now WHERE id=@run
                """, ("@run", runId),
                ("@now", DateTimeOffset.UtcNow.ToString("O")));
            Event(db, tx, runId, "EngineeringRunInterrupted",
                "Owner stopped unverified source generation");
        }
        tx.Commit();
    }

    public byte[] Zip(string taskId, string runId)
    {
        var result = Get(taskId, runId);
        if (result.Job.State != "Generated" || result.Files.Count == 0)
            throw new StoreFailure("BUILD_NOT_COMPLETE");
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var file in result.Files)
            {
                var entry = archive.CreateEntry(ProjectDraftStore.SafePath(file.Path),
                    CompressionLevel.Optimal);
                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(file.Content);
                stream.Write(bytes);
            }
        }
        return output.ToArray();
    }

    public IReadOnlyList<MafHandoffEvent> Handoffs(string taskId)
    {
        ValidId(taskId);
        var current = plans.Read(taskId).Plan;
        using var db = tasks.Open();
        var result = new List<MafHandoffEvent>();
        using var cmd = TaskStore.Command(db, null, """
            SELECT r.plan_revision,r.plan_sha256,r.spec_version,r.spec_sha256,
                   r.architecture_sha256,r.model,r.created_utc,
                   t.work_item_id,t.state,t.started_utc,t.completed_utc
            FROM engineering_runs r JOIN engineering_run_tasks t ON t.run_id=r.id
            WHERE r.task_id=@task ORDER BY r.created_utc,t.ordinal
            """, ("@task", taskId));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var revision = reader.GetInt32(0);
            var planSha = reader.GetString(1);
            var currentBinding = current?.Current == true &&
                current.Status == "Approved" && current.PlanSha256 == planSha;
            var baseEvent = new MafHandoffEvent(taskId, "EngineeringTaskAssigned",
                "TechnicalLead", "SoftwareEngineer", reader.GetInt32(2),
                reader.GetString(3), revision, reader.GetString(5),
                reader.GetString(6), currentBinding, planSha,
                reader.GetString(4));
            if (!reader.IsDBNull(9))
                result.Add(baseEvent with { CreatedUtc = reader.GetString(9) });
            if (!reader.IsDBNull(10))
                result.Add(baseEvent with {
                    Kind = "EngineeringTaskCompleted",
                    FromAgent = "SoftwareEngineer", ToAgent = "EngineeringBacklog",
                    CreatedUtc = reader.GetString(10)
                });
        }
        return result.AsReadOnly();
    }
}
