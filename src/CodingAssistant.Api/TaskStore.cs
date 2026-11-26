using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record TaskItem(
    string Id, string Title, string Objective, string Status,
    int CurrentVersion, string CreatedUtc);

public sealed record PlanItem(
    string TaskId, int Version, string Summary, string Model, string Scope,
    string PlanHash, string Status, string? ApprovedUtc, bool ExecutionEnabled);

public sealed record TaskView(TaskItem Task, PlanItem? Plan);
public sealed record CreateTaskRequest(string? Title, string? Objective, string? RequestId);
public sealed record DraftPlanRequest(string? Summary, int ExpectedVersion);
public sealed record ApprovePlanRequest(int Version, string? Approval);

public sealed class StoreFailure(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Single-owner SQLite control-plane records. This store is NOT an execution
/// grant: it never launches M1, Podman, ACPX, OpenCode or Ollama.
/// </summary>
public sealed class TaskStore
{
    public const string Model = "qwen3.5:4b";
    public const string Scope = "planning-only-no-execution";
    private static readonly Regex Hex32 =
        new("^[0-9a-f]{32}\\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly string _databasePath;
    private readonly string _connectionString;

    public TaskStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Private data directory is required");
        var full = Path.GetFullPath(directory);
        PrivateDataDirectory.Ensure(full);
        _databasePath = Path.Combine(full, "codingassistant.db");
        PrivateDataDirectory.ValidateFile(_databasePath);
        // Create the Unix database privately before SQLite can open it.
        if (!OperatingSystem.IsWindows() && !File.Exists(_databasePath))
        {
            using var created = new FileStream(_databasePath, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite, UnixCreateMode = UnixFileMode.UserRead |
                    UnixFileMode.UserWrite
            });
        }
        ValidateDatabasePaths();

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        using var connection = Open();
        Execute(connection, null, "PRAGMA journal_mode=WAL;");
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS tasks (
              id TEXT PRIMARY KEY NOT NULL,
              request_id TEXT NOT NULL UNIQUE,
              title TEXT NOT NULL,
              objective TEXT NOT NULL,
              status TEXT NOT NULL CHECK(status IN ('Draft','AwaitingApproval','Approved')),
              current_version INTEGER NOT NULL DEFAULT 0 CHECK(current_version >= 0),
              created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS qa_skill_use_events (
              task_id TEXT NOT NULL REFERENCES tasks(id),
              request_id TEXT NOT NULL,
              qa_id TEXT NOT NULL UNIQUE REFERENCES qa_reviews(id),
              skill_name TEXT NOT NULL,
              role TEXT NOT NULL,
              review_revision INTEGER NOT NULL CHECK(review_revision > 0),
              review_sha256 TEXT NOT NULL,
              content_sha256 TEXT NOT NULL,
              created_utc TEXT NOT NULL,
              PRIMARY KEY(task_id,request_id)
            );
            CREATE TABLE IF NOT EXISTS discovery_skill_use_events (
              task_id TEXT NOT NULL REFERENCES tasks(id),
              request_id TEXT NOT NULL,
              skill_name TEXT NOT NULL,
              role TEXT NOT NULL,
              review_revision INTEGER NOT NULL CHECK(review_revision > 0),
              review_sha256 TEXT NOT NULL,
              content_sha256 TEXT NOT NULL,
              topic TEXT NOT NULL CHECK(topic IN ('interview-turn','specification-draft')),
              expected_sequence INTEGER NOT NULL CHECK(expected_sequence >= 0),
              context_sha256 TEXT NOT NULL,
              model TEXT NOT NULL,
              status TEXT NOT NULL CHECK(status IN ('Admitted','ResponseReturned','CallFailed')),
              created_utc TEXT NOT NULL,
              finished_utc TEXT,
              PRIMARY KEY(task_id,request_id)
            );
            CREATE TABLE IF NOT EXISTS skill_use_events (
              task_id TEXT NOT NULL REFERENCES tasks(id),
              request_id TEXT NOT NULL,
              skill_name TEXT NOT NULL,
              role TEXT NOT NULL,
              review_revision INTEGER NOT NULL CHECK(review_revision > 0),
              review_sha256 TEXT NOT NULL,
              content_sha256 TEXT NOT NULL,
              spec_version INTEGER NOT NULL CHECK(spec_version > 0),
              spec_sha256 TEXT NOT NULL,
              topic TEXT NOT NULL,
              expected_decision_revision INTEGER NOT NULL CHECK(expected_decision_revision >= 0),
              model TEXT NOT NULL,
              status TEXT NOT NULL CHECK(status IN ('Admitted','ProposalReturned','CallFailed')),
              created_utc TEXT NOT NULL,
              finished_utc TEXT,
              PRIMARY KEY(task_id,request_id)
            );
            CREATE TABLE IF NOT EXISTS skill_review_events (
              task_id TEXT NOT NULL REFERENCES tasks(id),
              skill_name TEXT NOT NULL,
              revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 100),
              role TEXT NOT NULL,
              version TEXT NOT NULL,
              content_sha256 TEXT NOT NULL,
              license TEXT NOT NULL,
              review_sha256 TEXT NOT NULL,
              decision TEXT NOT NULL CHECK(decision IN ('ApproveLocalUse','Revoke')),
              local_use_confirmed INTEGER NOT NULL CHECK(local_use_confirmed IN (0,1)),
              created_utc TEXT NOT NULL,
              PRIMARY KEY(task_id,skill_name,revision)
            );
            CREATE TABLE IF NOT EXISTS plans (
              task_id TEXT NOT NULL REFERENCES tasks(id),
              version INTEGER NOT NULL CHECK(version > 0),
              summary TEXT NOT NULL,
              model TEXT NOT NULL,
              scope TEXT NOT NULL,
              plan_hash TEXT NOT NULL,
              status TEXT NOT NULL CHECK(status IN ('Pending','Approved','Superseded')),
              approved_utc TEXT,
              PRIMARY KEY(task_id,version)
            );
            """);
        EngineeringMessageStore.Initialize(connection);
        EngineeringDelegationStore.Initialize(connection);
        UpfrontTeamAuthorizationStore.Initialize(connection);
        AgentCommunicationStore.Initialize(connection);
        EngineeringActivityStore.Initialize(connection);
        LeadReviewStore.Initialize(connection);
        EngineeringIssueStore.Initialize(connection);
        IssueVerificationStore.Initialize(connection);
        AdaptiveWorkPlanStore.Initialize(connection);
        CollaborationStore.Initialize(connection);
        InferenceLeaseStore.Initialize(connection);
        SourceSmokeEvidenceStore.Initialize(connection);
        ValidateDatabasePaths();
    }

    private void ValidateDatabasePaths()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            PrivateDataDirectory.ValidateFile(_databasePath + suffix, sqliteSidecar: suffix.Length != 0);
    }

    internal SqliteConnection Open()
    {
        try { ValidateDatabasePaths(); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            // Fail closed on unreadable or unsafe data paths. Startup checks
            // remain strict; requests get a sanitized, retryable HTTP error.
            throw new StoreFailure("PRIVATE_DATA_UNAVAILABLE");
        }
        var db = new SqliteConnection(_connectionString);
        try
        {
            db.Open();
            Execute(db, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA secure_delete=ON;");
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    internal event Action<string>? EngineeringActivityChanged;
    internal void SignalEngineeringActivity(string taskId)
    {
        try { EngineeringActivityChanged?.Invoke(taskId); }
        catch (Exception) { /* Best-effort hint; durable history already committed. */ }
    }

    internal static SqliteCommand Command(
        SqliteConnection db, SqliteTransaction? tx, string statement,
        params (string Key, object Value)[] values)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = statement;
        foreach (var (key, value) in values)
            cmd.Parameters.AddWithValue(key, value);
        return cmd;
    }

    internal static int Execute(
        SqliteConnection db, SqliteTransaction? tx, string statement,
        params (string Key, object Value)[] values)
    {
        using var command = Command(db, tx, statement, values);
        return command.ExecuteNonQuery();
    }

    internal static string? Scalar(
        SqliteConnection db, SqliteTransaction? tx, string statement,
        params (string Key, object Value)[] values)
    {
        using var command = Command(db, tx, statement, values);
        return command.ExecuteScalar()?.ToString();
    }

    private static string ValidText(string? text, int maxLength)
    {
        if (text is null || text.Trim().Length is < 1 or > 4000)
            throw new StoreFailure("INVALID_INPUT");
        var clean = text.Trim();
        if (clean.Length > maxLength || clean.Any(ch => char.IsControl(ch) &&
                ch is not ('\n' or '\r' or '\t')))
            throw new StoreFailure("INVALID_INPUT");
        return clean;
    }

    private static void CheckId(string id)
    {
        if (!Hex32.IsMatch(id))
            throw new StoreFailure("INVALID_ID");
    }

    private static TaskItem ReadTask(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetString(3), reader.GetInt32(4), reader.GetString(5));

    private static PlanItem ReadPlan(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
        reader.GetString(3), reader.GetString(4), reader.GetString(5),
        reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
        ExecutionEnabled: false);

    internal static TaskItem? FindTask(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        using var cmd = Command(db, tx, """
            SELECT id,title,objective,status,current_version,created_utc
            FROM tasks WHERE id=@id
            """, ("@id", id));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadTask(reader) : null;
    }

    internal static PlanItem? FindPlan(
        SqliteConnection db, SqliteTransaction? tx, string id, int version)
    {
        using var cmd = Command(db, tx, """
            SELECT task_id,version,summary,model,scope,plan_hash,status,approved_utc
            FROM plans WHERE task_id=@id AND version=@version
            """, ("@id", id), ("@version", version));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadPlan(reader) : null;
    }

    public TaskItem CreateTask(CreateTaskRequest input)
    {
        var title = ValidText(input.Title, 140);
        var objective = ValidText(input.Objective, 4000);
        if (input.RequestId is null || !Hex32.IsMatch(input.RequestId))
            throw new StoreFailure("INVALID_REQUEST_ID");

        using var db = Open();
        using var tx = db.BeginTransaction();
        var priorId = Scalar(db, tx,
            "SELECT id FROM tasks WHERE request_id=@request",
            ("@request", input.RequestId));
        if (priorId is not null)
        {
            var prior = FindTask(db, tx, priorId)!;
            if (prior.Title != title || prior.Objective != objective)
                throw new StoreFailure("IDEMPOTENCY_CONFLICT");
            tx.Commit();
            return prior;
        }

        var id = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        Execute(db, tx, """
            INSERT INTO tasks (id,request_id,title,objective,status,current_version,created_utc)
            VALUES (@id,@request,@title,@objective,'Draft',0,@utc)
            """,
            ("@id", id), ("@request", input.RequestId),
            ("@title", title), ("@objective", objective), ("@utc", timestamp));
        tx.Commit();
        return new TaskItem(id, title, objective, "Draft", 0, timestamp);
    }

    /// <summary>
    /// Permanently removes a project and its persisted control-plane history.
    /// No disk workspace or other project is touched. All child removals and
    /// the task row removal are atomic with foreign-key enforcement enabled.
    /// Running draft/runner operations must first be cancelled or completed.
    /// </summary>
    public void DeleteTask(string id)
    {
        CheckId(id);
        using var db = Open();
        using var tx = db.BeginTransaction();
        _ = FindTask(db, tx, id) ?? throw new StoreFailure("NOT_FOUND");

        CollaborationStore.RequireNoActive(db,tx,id);
        if(Scalar(db,tx,"SELECT 1 FROM engineering_source_smoke WHERE task_id=@id AND state='Running'",("@id",id)) is not null)
            throw new StoreFailure("BUILD_BUSY");
        var runningDraft = Scalar(db, tx, """
            SELECT 1 FROM project_drafts
            WHERE task_id=@id AND state='Running' LIMIT 1
            """, ("@id", id));
        if (runningDraft is not null)
            throw new StoreFailure("BUILD_BUSY");
        if (Scalar(db, tx, "SELECT 1 FROM lead_reviews WHERE task_id=@id AND state='Running' LIMIT 1", ("@id",id)) is not null)
            throw new StoreFailure("BUILD_BUSY");
        var runningQa = Scalar(db, tx, """
            SELECT 1 FROM qa_reviews WHERE task_id=@id AND state='Running'
            LIMIT 1
            """, ("@id", id));
        if (runningQa is not null)
            throw new StoreFailure("BUILD_BUSY");
        var runningEngineering = Scalar(db, tx, """
            SELECT 1 FROM engineering_runs
            WHERE task_id=@id AND state='Running' LIMIT 1
            """, ("@id", id));
        if (runningEngineering is not null)
            throw new StoreFailure("BUILD_BUSY");
        var runningExecution = Scalar(db, tx, """
            SELECT 1 FROM run_executions AS e
            JOIN run_intents AS r ON r.id=e.run_id
            WHERE r.task_id=@id AND e.state IN ('Starting','Running','Cancelling','RecoveryBlocked')
            LIMIT 1
            """, ("@id", id));
        if (runningExecution is not null)
            throw new StoreFailure("EXECUTION_BUSY");

        // These tables have real foreign keys without ON DELETE CASCADE.
        // Delete leaf records before parents rather than disabling FK checks.
        foreach (var sql in new[] {
            "DELETE FROM project_draft_files WHERE draft_id IN (SELECT id FROM project_drafts WHERE task_id=@id)",
            "DELETE FROM project_draft_events WHERE draft_id IN (SELECT id FROM project_drafts WHERE task_id=@id)",
            "DELETE FROM project_draft_architecture WHERE draft_id IN (SELECT id FROM project_drafts WHERE task_id=@id)",
            "DELETE FROM task_build_events WHERE build_id IN (SELECT id FROM task_builds WHERE task_id=@id)",
            "DELETE FROM execution_events WHERE run_id IN (SELECT id FROM run_intents WHERE task_id=@id)",
            "DELETE FROM run_executions WHERE run_id IN (SELECT id FROM run_intents WHERE task_id=@id)",
            "DELETE FROM run_events WHERE run_id IN (SELECT id FROM run_intents WHERE task_id=@id)",
            "DELETE FROM project_drafts WHERE task_id=@id",
            "DELETE FROM task_builds WHERE task_id=@id",
            "DELETE FROM run_intents WHERE task_id=@id",
            "DELETE FROM plans WHERE task_id=@id",
            "DELETE FROM agent_role_models WHERE task_id=@id",
            "DELETE FROM qa_skill_use_events WHERE task_id=@id",
            "DELETE FROM discovery_skill_use_events WHERE task_id=@id",
            "DELETE FROM skill_use_events WHERE task_id=@id",
            "DELETE FROM skill_review_events WHERE task_id=@id",
            "DELETE FROM html_artifacts WHERE task_id=@id",
            "DELETE FROM repair_triage_decisions WHERE proposal_id IN (SELECT id FROM repair_proposals WHERE task_id=@id)",
            "DELETE FROM engineering_collaboration_repairs WHERE task_id=@id",
            "DELETE FROM repair_cycles WHERE task_id=@id",
            "DELETE FROM repair_proposals WHERE task_id=@id",
            "DELETE FROM lead_review_files WHERE review_id IN (SELECT id FROM lead_reviews WHERE task_id=@id)",
            "DELETE FROM lead_reviews WHERE task_id=@id",
            "DELETE FROM qa_static_preflight WHERE qa_id IN (SELECT id FROM qa_reviews WHERE task_id=@id)",
            "DELETE FROM qa_review_files WHERE qa_id IN (SELECT id FROM qa_reviews WHERE task_id=@id)",
            "DELETE FROM qa_review_events WHERE qa_id IN (SELECT id FROM qa_reviews WHERE task_id=@id)",
            "DELETE FROM qa_reviews WHERE task_id=@id",
            "DELETE FROM atlas_repository_files WHERE import_id IN (SELECT id FROM atlas_repository_imports WHERE task_id=@id)",
            "DELETE FROM atlas_repository_imports WHERE task_id=@id",
            "DELETE FROM engineering_atlas_snapshots WHERE run_id IN (SELECT id FROM engineering_runs WHERE task_id=@id)",
            "DELETE FROM engineering_atlas_graph_deltas WHERE run_id IN (SELECT id FROM engineering_runs WHERE task_id=@id)",
            "DELETE FROM engineering_atlas_events WHERE run_id IN (SELECT id FROM engineering_runs WHERE task_id=@id)",
            "DELETE FROM engineering_run_files WHERE run_id IN (SELECT id FROM engineering_runs WHERE task_id=@id)",
            "DELETE FROM engineering_run_events WHERE run_id IN (SELECT id FROM engineering_runs WHERE task_id=@id)",
            "DELETE FROM engineering_run_tasks WHERE run_id IN (SELECT id FROM engineering_runs WHERE task_id=@id)",
            "DELETE FROM engineering_runs WHERE task_id=@id",
            "DELETE FROM implementation_plans WHERE task_id=@id",
            "DELETE FROM maf_handoffs WHERE task_id=@id",
            "DELETE FROM architecture_decisions WHERE task_id=@id",
            "DELETE FROM interview_specs WHERE task_id=@id",
            "DELETE FROM interview_messages WHERE task_id=@id",
            "DELETE FROM interview_sessions WHERE task_id=@id",
            "DELETE FROM tasks WHERE id=@id",
        })
            Execute(db, tx, sql, ("@id", id));
        tx.Commit();
    }

    public IReadOnlyList<TaskItem> ListTasks(int limit = 50)
    {
        if (limit is < 1 or > 100)
            throw new StoreFailure("INVALID_LIMIT");
        using var db = Open();
        using var cmd = Command(db, null, """
            SELECT id,title,objective,status,current_version,created_utc
            FROM tasks ORDER BY created_utc DESC,id DESC LIMIT @limit
            """, ("@limit", limit));
        using var reader = cmd.ExecuteReader();
        var result = new List<TaskItem>();
        while (reader.Read())
            result.Add(ReadTask(reader));
        return result;
    }

    public TaskView GetTask(string id)
    {
        CheckId(id);
        using var db = Open();
        var task = FindTask(db, null, id) ?? throw new StoreFailure("NOT_FOUND");
        var plan = task.CurrentVersion > 0
            ? FindPlan(db, null, id, task.CurrentVersion)
            : null;
        return new TaskView(task, plan);
    }

    private sealed record HashInput(
        string Schema, string TaskId, int Version, string Summary,
        string Model, string Scope, bool ExecutionEnabled);

    public static string ComputePlanHash(
        string taskId, int version, string summary)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new HashInput("codingassistant-control-plan-v1", taskId, version,
                          summary, Model, Scope, false));
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    public PlanItem DraftPlan(string id, DraftPlanRequest input)
    {
        CheckId(id);
        var summary = ValidText(input.Summary, 1500);
        if (input.ExpectedVersion < 0)
            throw new StoreFailure("INVALID_VERSION");

        using var db = Open();
        using var tx = db.BeginTransaction();
        var task = FindTask(db, tx, id) ?? throw new StoreFailure("NOT_FOUND");
        if (task.CurrentVersion != input.ExpectedVersion)
            throw new StoreFailure("STALE_VERSION");
        var version = checked(task.CurrentVersion + 1);
        var hash = ComputePlanHash(id, version, summary);
        if (task.CurrentVersion > 0)
        {
            Execute(db, tx, """
                UPDATE plans SET status='Superseded' WHERE task_id=@id
                AND version=@old
                """, ("@id", id), ("@old", task.CurrentVersion));
        }
        Execute(db, tx, """
            INSERT INTO plans
              (task_id,version,summary,model,scope,plan_hash,status,approved_utc)
            VALUES (@id,@version,@summary,@model,@scope,@hash,'Pending',NULL)
            """, ("@id", id), ("@version", version),
                 ("@summary", summary), ("@model", Model),
                 ("@scope", Scope), ("@hash", hash));
        Execute(db, tx, """
            UPDATE tasks SET current_version=@version,status='AwaitingApproval'
            WHERE id=@id
            """, ("@version", version), ("@id", id));
        tx.Commit();
        return new PlanItem(id, version, summary, Model, Scope, hash,
            "Pending", null, ExecutionEnabled: false);
    }

    public PlanItem ApprovePlan(string id, ApprovePlanRequest input)
    {
        CheckId(id);
        if (input.Version < 1)
            throw new StoreFailure("INVALID_VERSION");
        using var db = Open();
        using var tx = db.BeginTransaction();
        var task = FindTask(db, tx, id) ?? throw new StoreFailure("NOT_FOUND");
        if (task.CurrentVersion != input.Version)
            throw new StoreFailure("STALE_VERSION");
        var plan = FindPlan(db, tx, id, input.Version)
                   ?? throw new StoreFailure("NOT_FOUND");
        if (!string.Equals(input.Approval, "APPROVE " + plan.PlanHash,
                           StringComparison.Ordinal))
            throw new StoreFailure("APPROVAL_MISMATCH");
        if (plan.Status == "Approved")
        {
            tx.Commit();
            return plan;
        }
        if (plan.Status != "Pending")
            throw new StoreFailure("STALE_VERSION");
        var approved = DateTimeOffset.UtcNow.ToString("O");
        Execute(db, tx, """
            UPDATE plans SET status='Approved',approved_utc=@utc
            WHERE task_id=@id AND version=@version AND status='Pending'
            """, ("@utc", approved), ("@id", id), ("@version", input.Version));
        Execute(db, tx,
            "UPDATE tasks SET status='Approved' WHERE id=@id", ("@id", id));
        tx.Commit();
        return plan with { Status = "Approved", ApprovedUtc = approved };
    }
}
