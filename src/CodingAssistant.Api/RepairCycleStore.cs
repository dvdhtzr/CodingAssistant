using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record RepairSelection(string Path, int Index);
public sealed record StartRepairCycleRequest(
    string? ProposalId, string? Confirmation,
    IReadOnlyList<RepairSelection>? Findings);
public sealed record RepairCycle(
    string Id, string TaskId, string ProposalId, string BaseRunId,
    string EngineeringRunId, string? QaReviewId, string BaseSourceSha256,
    string State, int Attempt, string? ErrorCode,
    IReadOnlyList<RepairFindingReference> Findings,
    string CreatedUtc, string UpdatedUtc);
public sealed record RepairCycleSnapshot(
    RepairCycle Cycle, EngineeringRunSnapshot Engineering,
    QaReviewSnapshot? Review, StaticWebPreflightReport StaticPreflight);

/// <summary>
/// Owner-authorized repair ledger. Admits a single, source-bound repair as a
/// NEW engineering run, copying all original files without changing them.
/// No agent, model or caller can create or expand its allowed file scope.
/// </summary>
public sealed class RepairCycleStore(
    TaskStore tasks, EngineeringRunStore engineering, QaReviewStore reviews,
    RepairProposalStore proposals)
{
    private static readonly Regex Id = new("^[0-9a-f]{32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS repair_cycles (
                id TEXT PRIMARY KEY NOT NULL,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                proposal_id TEXT NOT NULL UNIQUE REFERENCES repair_proposals(id),
                base_run_id TEXT NOT NULL REFERENCES engineering_runs(id),
                engineering_run_id TEXT NOT NULL UNIQUE REFERENCES engineering_runs(id),
                qa_review_id TEXT REFERENCES qa_reviews(id),
                base_source_sha256 TEXT NOT NULL,
                work_item_id TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN
                    ('Running','Generated','QaRunning','QaReviewed','Failed','Interrupted')),
                attempt INTEGER NOT NULL CHECK(attempt >= 1),
                error_code TEXT,
                findings_json TEXT NOT NULL,
                created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS repair_cycles_latest
                ON repair_cycles(task_id,created_utc DESC,id DESC);
            """);
        // Older installations have a physical CHECK(attempt BETWEEN 1 AND 3).
        // Migrate that table atomically, preserving all source-bound cycle
        // receipts, foreign keys, proposal uniqueness and historical attempts.
        var schema = TaskStore.Scalar(db, null, """
            SELECT sql FROM sqlite_master WHERE type='table' AND name='repair_cycles'
            """) ?? "";
        if (schema.Contains("BETWEEN 1 AND 3", StringComparison.OrdinalIgnoreCase))
        {
            using var migration = db.BeginTransaction();
            TaskStore.Execute(db, migration, """
                CREATE TABLE repair_cycles_unbounded (
                    id TEXT PRIMARY KEY NOT NULL,
                    task_id TEXT NOT NULL REFERENCES tasks(id),
                    proposal_id TEXT NOT NULL UNIQUE REFERENCES repair_proposals(id),
                    base_run_id TEXT NOT NULL REFERENCES engineering_runs(id),
                    engineering_run_id TEXT NOT NULL UNIQUE REFERENCES engineering_runs(id),
                    qa_review_id TEXT REFERENCES qa_reviews(id),
                    base_source_sha256 TEXT NOT NULL,
                    work_item_id TEXT NOT NULL,
                    state TEXT NOT NULL CHECK(state IN
                        ('Running','Generated','QaRunning','QaReviewed','Failed','Interrupted')),
                    attempt INTEGER NOT NULL CHECK(attempt >= 1),
                    error_code TEXT,
                    findings_json TEXT NOT NULL,
                    created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL
                );
                INSERT INTO repair_cycles_unbounded (
                    id,task_id,proposal_id,base_run_id,engineering_run_id,
                    qa_review_id,base_source_sha256,work_item_id,state,
                    attempt,error_code,findings_json,created_utc,updated_utc)
                SELECT id,task_id,proposal_id,base_run_id,engineering_run_id,
                    qa_review_id,base_source_sha256,work_item_id,state,
                    attempt,error_code,findings_json,created_utc,updated_utc
                FROM repair_cycles;
                DROP TABLE repair_cycles;
                ALTER TABLE repair_cycles_unbounded RENAME TO repair_cycles;
                CREATE INDEX repair_cycles_latest
                    ON repair_cycles(task_id,created_utc DESC,id DESC);
                """);
            migration.Commit();
        }
        TaskStore.Execute(db, null, """
            UPDATE repair_cycles SET state='Interrupted',
                error_code='PROCESS_INTERRUPTED',updated_utc=@now
            WHERE state IN ('Running','QaRunning')
            """, ("@now", DateTimeOffset.UtcNow.ToString("O")));
    }

    private const string Columns = """
        SELECT id,task_id,proposal_id,base_run_id,engineering_run_id,
               qa_review_id,base_source_sha256,state,attempt,error_code,
               findings_json,created_utc,updated_utc FROM repair_cycles
        """;

    private static RepairCycle Decode(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6), reader.GetString(7), reader.GetInt32(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        JsonSerializer.Deserialize<RepairFindingReference[]>(reader.GetString(10))
            ?? throw new StoreFailure("INVALID_STATE"),
        reader.GetString(11), reader.GetString(12));

    private static RepairCycle? Find(SqliteConnection db,
        SqliteTransaction? tx, string taskId, string? id = null)
    {
        using var cmd = TaskStore.Command(db, tx,
            Columns + (id is null
                ? " WHERE task_id=@task ORDER BY created_utc DESC,id DESC LIMIT 1"
                : " WHERE task_id=@task AND id=@id"),
            ("@task", taskId), ("@id", (object?)id ?? DBNull.Value));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Decode(reader) : null;
    }

    public RepairCycleSnapshot? Latest(string taskId)
    {
        CheckId(taskId);
        using var db = tasks.Open();
        _ = TaskStore.FindTask(db, null, taskId) ??
            throw new StoreFailure("NOT_FOUND");
        var cycle = Find(db, null, taskId);
        return cycle is null ? null : Snapshot(cycle);
    }

    public IReadOnlyList<RepairCycle> History(string taskId)
    {
        CheckId(taskId);
        using var db = tasks.Open();
        _ = TaskStore.FindTask(db, null, taskId) ??
            throw new StoreFailure("NOT_FOUND");
        var cycles = new List<RepairCycle>();
        using var cmd = TaskStore.Command(db, null,
            Columns + " WHERE task_id=@task ORDER BY created_utc DESC,id DESC",
            ("@task", taskId));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) cycles.Add(Decode(reader));
        return cycles.AsReadOnly();
    }

    public RepairCycleSnapshot Get(string taskId, string id)
    {
        CheckId(taskId);
        CheckId(id);
        using var db = tasks.Open();
        var cycle = Find(db, null, taskId, id) ??
            throw new StoreFailure("NOT_FOUND");
        return Snapshot(cycle);
    }

    private RepairCycleSnapshot Snapshot(RepairCycle cycle)
    {
        var run = engineering.Get(cycle.TaskId, cycle.EngineeringRunId);
        QaReviewSnapshot? qa = cycle.QaReviewId is null
            ? null : reviews.Get(cycle.TaskId, cycle.QaReviewId);
        return new(cycle, run, qa, StaticWebSourcePreflight.Inspect(run));
    }

    public RepairCycle Start(string taskId, StartRepairCycleRequest request,
        ImplementationPlan plan, string? agentModel = null)
    {
        CheckId(taskId);
        CheckId(request.ProposalId);
        if (plan.TaskId != taskId || plan.Status != "Approved" || !plan.Current)
            throw new StoreFailure("PLAN_NOT_APPROVED");
        var proposal = proposals.Latest(taskId);
        if (proposal is null || proposal.Id != request.ProposalId)
            throw new StoreFailure("SOURCE_CHANGED");
        if (request.Confirmation != "REPAIR " + proposal.SourceSha256 ||
            request.Findings is null || request.Findings.Count is < 1 or > 12)
            throw new StoreFailure("INVALID_INPUT");

        var selected = new List<RepairFindingReference>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requested in request.Findings)
        {
            if (requested is null || requested.Path is null ||
                requested.Index < 0 || !keys.Add(requested.Path + ":" + requested.Index))
                throw new StoreFailure("INVALID_INPUT");
            var finding = proposal.Findings.SingleOrDefault(f =>
                string.Equals(f.Path, requested.Path, StringComparison.OrdinalIgnoreCase)
                && f.Index == requested.Index);
            if (finding is null) throw new StoreFailure("INVALID_INPUT");
            selected.Add(finding);
        }
        var allowedPaths = selected.Select(f => f.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // One focused source file per cycle, and all its findings together.
        // More files can be addressed in later, independently reviewed cycles.
        if (allowedPaths.Length != 1)
            throw new StoreFailure("REPAIR_SCOPE_TOO_LARGE");

        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        if (TaskStore.FindTask(db, tx, taskId) is null)
            throw new StoreFailure("NOT_FOUND");
        var previous = TaskStore.Scalar(db, tx, """
            SELECT id FROM repair_cycles
            WHERE proposal_id=@proposal AND task_id=@task
            """, ("@proposal", proposal.Id), ("@task", taskId));
        if (previous is not null)
            throw new StoreFailure("IDEMPOTENCY_CONFLICT");
        var countValue = TaskStore.Scalar(db, tx, """
            SELECT COUNT(*) FROM repair_cycles WHERE task_id=@task
            """, ("@task", taskId));
        var attempt = int.Parse(countValue ?? "0") + 1;
        // Each next cycle requires fresh owner triage and exact source-bound
        // approval. Count is historical information, not an automatic ban.
        if (TaskStore.Scalar(db, tx, """
            SELECT 1 FROM repair_cycles
            WHERE task_id=@task AND state IN ('Running','QaRunning') LIMIT 1
            """, ("@task", taskId)) is not null)
            throw new StoreFailure("BUILD_BUSY");
        if (TaskStore.Scalar(db, tx, """
            SELECT id FROM engineering_runs WHERE task_id=@task
            ORDER BY created_utc DESC,id DESC LIMIT 1
            """, ("@task", taskId)) != proposal.EngineeringRunId ||
            TaskStore.Scalar(db, tx, """
            SELECT id FROM qa_reviews WHERE task_id=@task
            ORDER BY created_utc DESC,id DESC LIMIT 1
            """, ("@task", taskId)) != proposal.QaReviewId ||
            TaskStore.Scalar(db, tx, """
            SELECT state FROM qa_reviews WHERE id=@qa AND task_id=@task
            """, ("@qa", proposal.QaReviewId), ("@task", taskId)) != "Reviewed")
            throw new StoreFailure("SOURCE_CHANGED");
        if (TaskStore.Scalar(db, tx, """
            SELECT 1 FROM engineering_runs WHERE task_id=@task
            AND state='Running' LIMIT 1
            """, ("@task", taskId)) is not null)
            throw new StoreFailure("BUILD_BUSY");

        EngineeringRun baseRun;
        using (var cmd = TaskStore.Command(db, tx, """
            SELECT id,task_id,plan_revision,plan_sha256,spec_version,spec_sha256,
                architecture_sha256,model,state,phase,error_code,created_utc,
                updated_utc FROM engineering_runs WHERE id=@run AND task_id=@task
            """, ("@run", proposal.EngineeringRunId), ("@task", taskId)))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) throw new StoreFailure("SOURCE_CHANGED");
            baseRun = new EngineeringRun(reader.GetString(0), reader.GetString(1),
                reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetString(11), reader.GetString(12));
        }
        if (baseRun.State != "Generated" ||
            baseRun.PlanSha256 != proposal.PlanSha256 ||
            baseRun.PlanSha256 != plan.PlanSha256 ||
            baseRun.PlanRevision != proposal.PlanRevision ||
            baseRun.SpecificationSha256 != proposal.SpecificationSha256 ||
            baseRun.ArchitectureSha256 != proposal.ArchitectureSha256)
            throw new StoreFailure("SOURCE_CHANGED");
        EngineeringRunStore.RequireBinding(db, tx, baseRun);

        var files = new List<EngineeringSourceFile>();
        using (var cmd = TaskStore.Command(db, tx, """
            SELECT path,purpose,content,sha256,work_item_id,updated_utc
            FROM engineering_run_files WHERE run_id=@run ORDER BY path
            """, ("@run", baseRun.Id)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                files.Add(new EngineeringSourceFile(reader.GetString(0),
                    reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5)));
        if (QaReviewStore.Fingerprint(files) != proposal.SourceSha256 ||
            files.Count is < 1 or > EngineeringRunStore.MaxDistinctFiles ||
            files.Sum(f => (long)f.Content.Length) > EngineeringRunStore.MaxTotalCharacters ||
            files.Sum(f => (long)f.Content.Length) > 48000 ||
            !files.Any(f => string.Equals(f.Path, allowedPaths[0],
                StringComparison.OrdinalIgnoreCase)))
            throw new StoreFailure("REPAIR_SCOPE_TOO_LARGE");

        // The owner selects a specific QA finding. An independently persisted
        // triage decision must explicitly classify it as a code defect.
        foreach (var finding in selected)
        {
            if (TaskStore.Scalar(db, tx, """
                SELECT classification FROM repair_triage_decisions
                WHERE proposal_id=@proposal AND path=@path AND finding_index=@index
                """, ("@proposal", proposal.Id), ("@path", finding.Path),
                ("@index", finding.Index)) != "ImplementationDefect")
                throw new StoreFailure("REPAIR_NOT_AUTHORIZED");
        }

        // The execution context reflects the persisted owner decision,
        // rather than the model's earlier conservative QA classification.
        selected = selected.Select(f => f with {
            Classification = "ImplementationDefect"
        }).ToList();

        var originalWorkItem = files.First(f =>
            string.Equals(f.Path, allowedPaths[0], StringComparison.OrdinalIgnoreCase))
            .LastWorkItemId;
        var workItem = plan.Outline.Tasks.FirstOrDefault(t =>
            t.Id == originalWorkItem) ?? plan.Outline.Tasks[0];
        var now = DateTimeOffset.UtcNow.ToString("O");
        var runId = Guid.NewGuid().ToString("N");
        var cycleId = Guid.NewGuid().ToString("N");
        try
        {
            TaskStore.Execute(db, tx, """
                INSERT INTO engineering_runs (
                    id,task_id,plan_revision,plan_sha256,spec_version,spec_sha256,
                    architecture_sha256,model,state,phase,created_utc,updated_utc)
                VALUES (@id,@task,@revision,@plan,@sv,@spec,@arch,@model,
                    'Running','RepairQueued',@now,@now)
                """, ("@id", runId), ("@task", taskId),
                ("@revision", baseRun.PlanRevision),
                ("@plan", baseRun.PlanSha256),
                ("@sv", baseRun.SpecificationVersion),
                ("@spec", baseRun.SpecificationSha256),
                ("@arch", baseRun.ArchitectureSha256),
                ("@model", agentModel ?? baseRun.Model), ("@now", now));
            TaskStore.Execute(db, tx, """
                INSERT INTO engineering_run_tasks
                    (run_id,work_item_id,ordinal,title,state)
                VALUES (@run,@work,0,'Repair confirmed QA findings','Pending')
                """, ("@run", runId), ("@work", workItem.Id));
            TaskStore.Execute(db, tx, """
                INSERT INTO engineering_run_files
                    (run_id,path,purpose,content,sha256,work_item_id,updated_utc)
                SELECT @newRun,path,purpose,content,sha256,work_item_id,@now
                FROM engineering_run_files WHERE run_id=@base
                """, ("@newRun", runId), ("@now", now), ("@base", baseRun.Id));
            TaskStore.Execute(db, tx, """
                INSERT INTO repair_cycles
                    (id,task_id,proposal_id,base_run_id,engineering_run_id,
                     base_source_sha256,work_item_id,state,attempt,
                     findings_json,created_utc,updated_utc)
                VALUES (@id,@task,@proposal,@base,@run,@sha,@work,
                    'Running',@attempt,@findings,@now,@now)
                """, ("@id", cycleId), ("@task", taskId),
                ("@proposal", proposal.Id), ("@base", baseRun.Id),
                ("@run", runId), ("@sha", proposal.SourceSha256),
                ("@work", workItem.Id), ("@attempt", attempt),
                ("@findings", JsonSerializer.Serialize(selected)), ("@now", now));
            TaskStore.Execute(db, tx, """
                INSERT INTO engineering_run_events
                    (run_id,sequence,kind,detail,created_utc)
                VALUES (@run,1,'RepairRunAdmitted',
                    'Owner-authorized QA repair; source cloned unchanged, not tested',@now)
                """, ("@run", runId), ("@now", now));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new StoreFailure("BUILD_BUSY");
        }
        EngineeringActivityStore.Record(db,tx,taskId,"RepairRunAdmitted","SoftwareEngineer",
            "RepairCycle",cycleId,sourceSha256:proposal.SourceSha256);
        tx.Commit();
        tasks.SignalEngineeringActivity(taskId);
        return new RepairCycle(cycleId, taskId, proposal.Id, baseRun.Id,
            runId, null, proposal.SourceSha256, "Running", attempt, null,
            selected.AsReadOnly(), now, now);
    }

    public string WorkItemId(string taskId, string cycleId)
    {
        CheckId(taskId); CheckId(cycleId);
        using var db = tasks.Open();
        return TaskStore.Scalar(db, null, """
            SELECT work_item_id FROM repair_cycles
            WHERE id=@id AND task_id=@task
            """, ("@id", cycleId), ("@task", taskId)) ??
            throw new StoreFailure("NOT_FOUND");
    }

    public void SetState(string taskId, string cycleId, string state,
        string? error = null, string? qaId = null)
    {
        CheckId(taskId); CheckId(cycleId);
        if (state is not ("Generated" or "QaRunning" or "QaReviewed"
            or "Failed" or "Interrupted"))
            throw new StoreFailure("INVALID_INPUT");
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var current = Find(db, tx, taskId, cycleId) ??
            throw new StoreFailure("NOT_FOUND");
        if (current.State is not ("Running" or "Generated" or "QaRunning") ||
            state == "QaRunning" && (current.State != "Generated" || qaId is null) ||
            state == "QaReviewed" && current.State != "QaRunning")
            throw new StoreFailure("INVALID_STATE");
        TaskStore.Execute(db, tx, """
            UPDATE repair_cycles SET state=@state,error_code=@error,
                qa_review_id=COALESCE(@qa,qa_review_id),updated_utc=@now
            WHERE id=@id AND task_id=@task
            """, ("@state", state), ("@error", (object?)error ?? DBNull.Value),
            ("@qa", (object?)qaId ?? DBNull.Value),
            ("@now", DateTimeOffset.UtcNow.ToString("O")),
            ("@id", cycleId), ("@task", taskId));
        EngineeringActivityStore.Record(db,tx,taskId,"Repair"+state,
            state is "QaRunning" or "QaReviewed" ? "QaEngineer" : "SoftwareEngineer",
            "RepairCycle",cycleId);
        tx.Commit();
        tasks.SignalEngineeringActivity(taskId);
    }

    private static void CheckId(string? id)
    {
        if (id is null || !Id.IsMatch(id))
            throw new StoreFailure("INVALID_ID");
    }
}
