using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record StartEngineeringRunRequest(
    int PlanRevision, string? PlanSha256, string? ResumeRunId = null,
    AdaptivePlanSelection? WorkPlan = null);
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
                    ('Pending','Running','Completed','Failed','Interrupted')),
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
        EngineeringAtlasLedger.EnsureSchema(db);
    }

    public void MarkInterruptedOnStartup()
    {
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        // Record every restart interruption with the same transaction that
        // updates the run and active assignment. Previously a running process
        // would silently turn into Interrupted with NO terminal audit event.
        var interruptedRuns = new List<string>();
        using (var command = TaskStore.Command(db, tx, """
            SELECT id FROM engineering_runs WHERE state='Running'
            ORDER BY created_utc,id
            """))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                interruptedRuns.Add(reader.GetString(0));
        TaskStore.Execute(db, tx, """
            UPDATE engineering_run_tasks SET state='Interrupted'
            WHERE state='Running' AND run_id IN (
                SELECT id FROM engineering_runs WHERE state='Running')
            """);
        TaskStore.Execute(db, tx, """
            UPDATE engineering_runs SET state='Interrupted',
                phase='RestartInterrupted',error_code='PROCESS_INTERRUPTED',
                updated_utc=@now WHERE state='Running'
            """, ("@now", DateTimeOffset.UtcNow.ToString("O")));
        foreach (var runId in interruptedRuns)
            Event(db, tx, runId, "EngineeringRunInterrupted",
                "PROCESS_INTERRUPTED: app restarted while generation was active; completed source remains saved");

        // Recover an audit gap left by older versions for an ALREADY
        // interrupted run. This only appends a missing explanatory receipt:
        // never reactivates a task, changes its approval or alters source.
        var unreported = new List<string>();
        using (var command = TaskStore.Command(db, tx, """
            SELECT id FROM engineering_runs AS r
            WHERE state='Interrupted' AND error_code='PROCESS_INTERRUPTED'
              AND NOT EXISTS (
                SELECT 1 FROM engineering_run_events e
                WHERE e.run_id=r.id AND e.kind='EngineeringRunInterrupted')
            ORDER BY created_utc,id
            """))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                unreported.Add(reader.GetString(0));
        foreach (var runId in unreported)
            Event(db, tx, runId, "EngineeringRunInterrupted",
                "Recovered missing PROCESS_INTERRUPTED receipt from an earlier app restart; saved source unchanged");
        tx.Commit();
    }

    private static EngineeringRun ReadRun(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
        reader.GetString(3), reader.GetInt32(4), reader.GetString(5),
        reader.GetString(6), reader.GetString(7), reader.GetString(8),
        reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.GetString(11), reader.GetString(12));

    internal static EngineeringRun? Find(SqliteConnection db, SqliteTransaction? tx,
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

    internal static void RequireBinding(SqliteConnection db, SqliteTransaction tx,
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
        if (kind is "EngineeringTaskStarted" or "EngineeringTaskCompleted")
            return; // The richer atomic assignment receipt supplies this activity.
        var job = Find(db,tx,runId);
        if (job is not null)
            EngineeringActivityStore.Record(db,tx,job.TaskId,kind,"SoftwareEngineer",
                "EngineeringRun",runId,long.Parse(TaskStore.Scalar(db,tx,
                    "SELECT MAX(sequence) FROM engineering_run_events WHERE run_id=@run",
                    ("@run",runId))!),model:job.Model);
    }

    public EngineeringRun Start(string taskId, ImplementationPlan approved,
        string? agentModel = null,AdaptivePlanSelection? selection=null)
    {
        ValidId(taskId);
        if (approved.TaskId != taskId || approved.Status != "Approved" ||
            !approved.Current || !LocalInterviewModel.AllowedModel(agentModel ?? approved.Model))
            throw new StoreFailure("PLAN_NOT_APPROVED");
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var candidate = new EngineeringRun(id, taskId, approved.Revision,
            approved.PlanSha256, approved.SpecificationVersion,
            approved.SpecificationSha256, approved.ArchitectureSha256,
            agentModel ?? approved.Model, "Running", "Queued", null, now, now);
        RequireBinding(db, tx, candidate);
        CollaborationStore.RequireNoActive(db,tx,taskId);
        var adaptive=selection is null?null:AdaptiveWorkPlanStore.RequireSelected(db,tx,taskId,
            approved.PlanSha256,selection);
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
                ("@model", candidate.Model), ("@now", now));
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
        if(adaptive is not null)AdaptiveWorkPlanStore.Bind(db,tx,candidate,adaptive);
        Event(db, tx, id, "EngineeringRunAdmitted",
            "Owner-approved plan v" + approved.Revision +
            " assigned to SoftwareEngineer; unverified source only");
        tx.Commit();
        tasks.SignalEngineeringActivity(taskId);
        return candidate;
    }

    /// <summary>
    /// Creates a NEW run from the exact latest failed/interrupted source.
    /// The original run is immutable. Only a contiguous, completed prefix of
    /// owner-approved tasks is carried forward; the first incomplete task is
    /// retried through the normal MAF/host boundary. No execution is authorized.
    /// </summary>
    public EngineeringRun Resume(string taskId, ImplementationPlan approved,
        string sourceRunId, string? agentModel = null)
    {
        ValidId(taskId);
        ValidId(sourceRunId);
        if (approved.TaskId != taskId || approved.Status != "Approved" ||
            !approved.Current || !LocalInterviewModel.AllowedModel(agentModel ?? approved.Model))
            throw new StoreFailure("PLAN_NOT_APPROVED");

        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        CollaborationStore.RequireNoActive(db,tx,taskId);
        var original = Find(db, tx, sourceRunId);
        if (original is null || original.TaskId != taskId)
            throw new StoreFailure("NOT_FOUND");
        if (original.State is not ("Failed" or "Interrupted"))
            throw new StoreFailure("RESUME_NOT_AVAILABLE");
        RequireBinding(db, tx, original);
        if (original.PlanRevision != approved.Revision ||
            original.PlanSha256 != approved.PlanSha256 ||
            original.SpecificationVersion != approved.SpecificationVersion ||
            original.SpecificationSha256 != approved.SpecificationSha256 ||
            original.ArchitectureSha256 != approved.ArchitectureSha256)
            throw new StoreFailure("STALE_IMPLEMENTATION_PLAN");

        var latest = TaskStore.Scalar(db, tx, """
            SELECT id FROM engineering_runs WHERE task_id=@task
            ORDER BY created_utc DESC,id DESC LIMIT 1
            """, ("@task", taskId));
        if (latest != sourceRunId)
            throw new StoreFailure("STALE_SOURCE_RUN");

        var adaptive=AdaptiveWorkPlanStore.RequireBound(db,tx,original);
        var carried = new List<EngineeringAssignedTask>();
        var ordinal = 0;
        var sawIncomplete = false;
        using (var cmd = TaskStore.Command(db, tx, """
            SELECT work_item_id,ordinal,title,state,summary,started_utc,completed_utc
            FROM engineering_run_tasks WHERE run_id=@run ORDER BY ordinal
            """, ("@run", sourceRunId)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (ordinal >= approved.Outline.Tasks.Count ||
                    reader.GetInt32(1) != ordinal ||
                    reader.GetString(0) != approved.Outline.Tasks[ordinal].Id ||
                    reader.GetString(2) != approved.Outline.Tasks[ordinal].Title)
                    throw new StoreFailure("STALE_IMPLEMENTATION_PLAN");
                var state = reader.GetString(3);
                if (state == "Completed")
                {
                    if ((adaptive is null && sawIncomplete) || reader.IsDBNull(6) || reader.IsDBNull(4))
                        throw new StoreFailure("RESUME_NOT_AVAILABLE");
                    carried.Add(new EngineeringAssignedTask(reader.GetString(0),
                        ordinal, reader.GetString(2), state,
                        reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.GetString(6)));
                }
                else if (state is "Failed" or "Interrupted" or "Pending")
                    sawIncomplete = true;
                else
                    throw new StoreFailure("RESUME_NOT_AVAILABLE");
                ordinal++;
            }
        }
        var partial=adaptive is null?null:AdaptiveWorkPlanStore.PartialCheckpoint(db,tx,original,adaptive,carried);
        if (ordinal != approved.Outline.Tasks.Count || !sawIncomplete ||
            (carried.Count == 0 && partial is null) || carried.Count == ordinal)
            throw new StoreFailure("RESUME_NOT_AVAILABLE");

        if(adaptive is not null && adaptive.Packages.Where(p=>carried.Any(c=>c.Id==p.Id))
            .Any(p=>p.DependsOn.Any(d=>!carried.Any(c=>c.Id==d))))
            throw new StoreFailure("RESUME_NOT_AVAILABLE");
        var savedCount = 0;
        long characters = 0;
        using (var cmd = TaskStore.Command(db, tx, """
            SELECT path,content,sha256,work_item_id
            FROM engineering_run_files WHERE run_id=@run
            """, ("@run", sourceRunId)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var path = reader.GetString(0);
                var content = reader.GetString(1);
                var sha = reader.GetString(2);
                var owner = reader.GetString(3);
                if ((!carried.Any(t => t.Id == owner) &&
                     !(partial?.WorkItemId==owner && partial.AllowedPaths.Contains(path,StringComparer.OrdinalIgnoreCase))) ||
                    ProjectDraftStore.SafePath(path) != path ||
                    Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes(content))).ToLowerInvariant() != sha)
                    throw new StoreFailure("SOURCE_CHANGED");
                savedCount++;
                characters += content.Length;
            }
        }
        if (savedCount == 0 || savedCount > MaxDistinctFiles ||
            characters > MaxTotalCharacters)
            throw new StoreFailure("RESUME_NOT_AVAILABLE");

        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var job = original with {
            Id = id, Model = agentModel ?? original.Model,
            State = "Running", Phase = "Resumed",
            ErrorCode = null, CreatedUtc = now, UpdatedUtc = now
        };
        try
        {
            TaskStore.Execute(db, tx, """
                INSERT INTO engineering_runs(
                    id,task_id,plan_revision,plan_sha256,spec_version,spec_sha256,
                    architecture_sha256,model,state,phase,created_utc,updated_utc)
                VALUES(@id,@task,@revision,@sha,@sv,@ss,@arch,@model,
                       'Running','Resumed',@now,@now)
                """, ("@id", id), ("@task", taskId),
                ("@revision", job.PlanRevision), ("@sha", job.PlanSha256),
                ("@sv", job.SpecificationVersion),
                ("@ss", job.SpecificationSha256),
                ("@arch", job.ArchitectureSha256),
                ("@model", job.Model), ("@now", now));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new StoreFailure("BUILD_BUSY");
        }

        foreach (var item in approved.Outline.Tasks.Select((task, i) => (task, i)))
        {
            var prior = carried.SingleOrDefault(t=>t.Id==item.task.Id);
            TaskStore.Execute(db, tx, """
                INSERT INTO engineering_run_tasks(
                    run_id,work_item_id,ordinal,title,state,summary,started_utc,completed_utc)
                VALUES(@run,@item,@ordinal,@title,@state,@summary,@started,@completed)
                """, ("@run", id), ("@item", item.task.Id),
                ("@ordinal", item.i), ("@title", item.task.Title),
                ("@state", prior is null ? "Pending" : "Completed"),
                ("@summary", (object?)prior?.Summary ?? DBNull.Value),
                ("@started", (object?)prior?.StartedUtc ?? DBNull.Value),
                ("@completed", (object?)prior?.CompletedUtc ?? DBNull.Value));
        }
        var copied = TaskStore.Execute(db, tx, """
            INSERT INTO engineering_run_files(
                run_id,path,purpose,content,sha256,work_item_id,updated_utc)
            SELECT @new,path,purpose,content,sha256,work_item_id,updated_utc
            FROM engineering_run_files WHERE run_id=@previous
            """, ("@new", id), ("@previous", sourceRunId));
        if (copied != savedCount)
            throw new StoreFailure("SOURCE_CHANGED");

        if(adaptive is not null)
        {
            AdaptiveWorkPlanStore.Bind(db,tx,job,adaptive);
            if(partial is not null)AdaptiveWorkPlanStore.CarrySubpackages(db,tx,job,sourceRunId,partial);
        }
        Event(db, tx, id, "EngineeringRunResumed",
            "Carried forward " + carried.Count + " approved task(s), " +
            savedCount + " saved file(s) from run " + sourceRunId +
            "; remaining assignments unverified");
        tx.Commit();
        tasks.SignalEngineeringActivity(taskId);
        return job;
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
        EngineeringDelegationStore.RequireRoutineWrite(db,tx,job);
        _=StartTaskCore(db,tx,job,workItemId);
        tx.Commit();
        tasks.SignalEngineeringActivity(taskId);
    }

    public bool HasAdaptiveBinding(string taskId,string runId)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        var job=Find(db,tx,runId);
        if(job is null || job.TaskId!=taskId)throw new StoreFailure("NOT_FOUND");
        var result=AdaptiveWorkPlanStore.Bound(db,tx,runId) is not null;
        tx.Commit();return result;
    }

    public AdaptiveWorkPackage? StartNextAdaptiveTask(string taskId,string runId)
    {
        ValidId(taskId);ValidId(runId);
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var job=Find(db,tx,runId);
        if(job is null || job.TaskId!=taskId)throw new StoreFailure("NOT_FOUND");
        if(job.State!="Running")throw new StoreFailure("INVALID_STATE");
        RequireBinding(db,tx,job);
        var plan=AdaptiveWorkPlanStore.RequireBound(db,tx,job)??throw new StoreFailure("STALE_WORK_PLAN");
        var next=AdaptiveWorkPlanStore.Next(plan.Packages,AdaptiveWorkPlanStore.States(db,tx,runId));
        if(next is null){tx.Commit();return null;}
        var package=StartTaskCore(db,tx,job,next);
        tx.Commit();tasks.SignalEngineeringActivity(taskId);return package;
    }

    public AdaptiveWorkSubpackage? StartNextSubpackage(string taskId,string runId,string workItemId)
    {
        ValidId(taskId);ValidId(runId);
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var job=Find(db,tx,runId);
        if(job is null || job.TaskId!=taskId)throw new StoreFailure("NOT_FOUND");
        if(job.State!="Running")throw new StoreFailure("INVALID_STATE");
        RequireBinding(db,tx,job);
        var step=AdaptiveWorkPlanStore.AdmitNextSubpackage(db,tx,job,workItemId);
        if(step is not null)Event(db,tx,runId,"EngineeringSubpackageStarted",
            workItemId+" / "+step.Id+" dispatched within approved parent scope");
        tx.Commit();tasks.SignalEngineeringActivity(taskId);return step;
    }

    private static AdaptiveWorkPackage? StartTaskCore(SqliteConnection db,SqliteTransaction tx,
        EngineeringRun job,string workItemId)
    {
        var runId=job.Id;
        var ordinal = TaskStore.Scalar(db, tx, """
            SELECT ordinal FROM engineering_run_tasks
            WHERE run_id=@run AND work_item_id=@item AND state='Pending'
            """, ("@run", runId), ("@item", workItemId));
        if (ordinal is null)
            throw new StoreFailure("INVALID_STATE");
        var package=AdaptiveWorkPlanStore.Admit(db,tx,job,workItemId);
        if(package is null)
        {
            var incomplete = TaskStore.Scalar(db, tx, """
                SELECT 1 FROM engineering_run_tasks
                WHERE run_id=@run AND ordinal<@ordinal AND state!='Completed' LIMIT 1
                """, ("@run", runId), ("@ordinal", int.Parse(ordinal)));
            if (incomplete is not null)
                throw new StoreFailure("INVALID_STATE");
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        TaskStore.Execute(db, tx, """
            UPDATE engineering_run_tasks SET state='Running',started_utc=@now
            WHERE run_id=@run AND work_item_id=@item
            """, ("@run", runId), ("@item", workItemId), ("@now", now));
        Event(db, tx, runId, "EngineeringTaskStarted",
            "SoftwareEngineer received approved task " + workItemId);
        EngineeringMessageStore.Record(db, tx, job, workItemId, completed: false);
        return package;
    }

    /// <summary>
    /// Admit a read-only Code Atlas observation only for the exact currently
    /// running approved work item. Persist host-verified metadata without
    /// leaking a model query, source excerpt or arbitrary model narrative.
    /// </summary>
    public void RecordAtlasInspection(string taskId, string runId,
        string workItemId, AtlasInspectionObservation observation)
    {
        ValidId(taskId); ValidId(runId);
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
        var job = Find(db, tx, runId);
        if (job is null || job.TaskId != taskId)
            throw new StoreFailure("NOT_FOUND");
        if (job.State != "Running")
            throw new StoreFailure("INVALID_STATE");
        RequireBinding(db, tx, job);
        EngineeringDelegationStore.RequireRoutineWrite(db,tx,job,safe.Files);
        if (TaskStore.Scalar(db, tx, """
            SELECT 1 FROM engineering_run_tasks
            WHERE run_id=@run AND work_item_id=@item AND state='Running'
            """, ("@run", runId), ("@item", workItemId)) is null)
            throw new StoreFailure("INVALID_STATE");
        Event(db, tx, runId, "EngineerAtlasInspected",
            "SoftwareEngineer used " + observation.Name +
            " on accepted source revision " +
            observation.SourceRevision[..12] + " (" +
            observation.ResultCount + " results)");
        tx.Commit();
        tasks.SignalEngineeringActivity(taskId);
    }

    /// <summary>
    /// Model-free, bounded rejection metadata for a single authorized active
    /// assignment. Never persist model output, generated source or prompt text.
    /// A failure can now be diagnosed after the third rejected response.
    /// </summary>
    public void RecordOutputRejection(string taskId, string runId,
        string workItemId, EngineeringOutputRejection rejection)
    {
        ValidId(taskId);
        ValidId(runId);
        if (rejection.Attempt is < 1 or > 3 ||
            rejection.Category is not (
                "EMPTY_RESPONSE" or "OUTPUT_TOO_LONG" or "ROOT_SHAPE" or
                "EXTRA_FIELDS" or "SUMMARY" or "FILES_COUNT" or
                "FILE_SHAPE" or "FILE_EXTRA_FIELDS" or "FILE_PATH" or
                "FILE_PURPOSE" or "FILE_CONTENT" or "VERIFICATION_NOTES" or
                "SOURCE_SYNTAX" or "SOURCE_CONTRACT" or "JSON_SYNTAX" or
                "STATIC_WEB_PATH" or "STATIC_WEB_ENTRY" or
                "REPAIR_SCOPE" or "REPAIR_EDIT_INVALID" or "REPAIR_EDIT_NOT_UNIQUE" or
                "REPAIR_EDIT_NO_CHANGE" or "OUTPUT_TRUNCATED" or
                "MODEL_THINKING_ONLY" or "EMPTY_OR_INVALID_RESPONSE"))
            throw new StoreFailure("INVALID_INPUT");
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
        var previous = 0;
        using (var command = TaskStore.Command(db, tx, """
            SELECT detail FROM engineering_run_events
            WHERE run_id=@run AND kind='EngineerOutputRejected'
            """, ("@run", runId)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (reader.GetString(0).StartsWith(workItemId + ":",
                    StringComparison.Ordinal))
                    previous++;
        if (previous + 1 != rejection.Attempt)
            throw new StoreFailure("INVALID_STATE");
        Event(db, tx, runId, "EngineerOutputRejected",
            workItemId + ": attempt " + rejection.Attempt + "/3, category " +
            rejection.Category + "; no source saved for this attempt");
        tx.Commit();
        tasks.SignalEngineeringActivity(taskId);
    }

    public AtlasSourceEvent? CompleteTask(string taskId, string runId, string workItemId,
        EngineeringChangeSet changes,string? subpackageId=null)
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

        AdaptiveWorkPlanStore.RequireWrite(db,tx,job,workItemId,safe.Files,subpackageId);

        // Repair runs are allowed to replace only the exact owner-triaged
        // source paths. Enforce this INSIDE the same SQLite transaction that
        // persists source, independently of the agent and coordinator.
        var repairScopeJson = TaskStore.Scalar(db, tx, """
            SELECT findings_json FROM repair_cycles
            WHERE engineering_run_id=@run AND task_id=@task
            """, ("@run", runId), ("@task", taskId));
        if (repairScopeJson is not null)
        {
            var repairFindings =
                JsonSerializer.Deserialize<RepairFindingReference[]>(repairScopeJson)
                ?? throw new StoreFailure("INVALID_STATE");
            var allowedPaths = repairFindings.Select(f => f.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (allowedPaths.Length != 1 || safe.Files.Any(f =>
                !allowedPaths.Contains(f.Path, StringComparer.OrdinalIgnoreCase)))
                throw new StoreFailure("REPAIR_SCOPE_VIOLATION");
        }
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

        // Compare committed source hashes inside this same transaction. Atlas
        // events must never claim a file changed if the source save rolled back.
        var beforeAtlas = EngineeringAtlasLedger.CaptureHashes(db, tx, runId);
        AtlasSnapshot? previousGraph = null;
        try { previousGraph = EngineeringAtlasLedger.CaptureGraph(db, tx, runId); }
        catch (Exception)
        {
            // An indexer failure must not prevent saving authorized source.
            // The file ledger remains authoritative and can be replayed.
        }
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
        AtlasSnapshot? currentGraph = null;
        try { currentGraph = EngineeringAtlasLedger.CaptureGraph(db, tx, runId); }
        catch (Exception)
        {
            // Source-only work remains valid even if graph enrichment fails.
        }
        var atlasEvent = EngineeringAtlasLedger.Append(
            db, tx, runId, workItemId, beforeAtlas, previousGraph, currentGraph);
        if(subpackageId is not null)
        {
            var last=AdaptiveWorkPlanStore.CompleteSubpackage(db,tx,job,workItemId,subpackageId);
            Event(db,tx,runId,"EngineeringSubpackageCompleted",
                workItemId+" / "+subpackageId+" source persisted; not tested");
            if(!last)
            {
                TaskStore.Execute(db,tx,"UPDATE engineering_runs SET phase=@phase,updated_utc=@now WHERE id=@run",
                    ("@phase","SavedSubpackage:"+subpackageId),("@now",now),("@run",runId));
                tx.Commit();tasks.SignalEngineeringActivity(taskId);return atlasEvent;
            }
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
        EngineeringMessageStore.Record(db, tx, job, workItemId, completed: true);
        AdaptiveWorkPlanStore.Complete(db,tx,job,workItemId);
        tx.Commit();
        tasks.SignalEngineeringActivity(taskId);
        return atlasEvent;
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
            EngineeringDelegationStore.RequireRoutineWrite(db,tx,job);
            _=AdaptiveWorkPlanStore.RequireBound(db,tx,job);
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
        if (!success)
            TaskStore.Execute(db, tx, """
                UPDATE engineering_run_tasks SET state='Failed'
                WHERE run_id=@run AND state='Running'
                """, ("@run", runId));
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
        tasks.SignalEngineeringActivity(taskId);
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
                UPDATE engineering_run_tasks SET state='Interrupted'
                WHERE run_id=@run AND state='Running'
                """, ("@run", runId));
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
        tasks.SignalEngineeringActivity(taskId);
    }

    public byte[] Zip(string taskId, string runId)
    {
        var result = Get(taskId, runId);
        if (!QaReviewStore.IsReviewableSource(
                result.Job.State, result.Files.Count))
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
                reader.GetString(4), reader.GetString(7));
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
