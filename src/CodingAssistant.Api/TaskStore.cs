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
        new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly string _databasePath;
    private readonly string _connectionString;

    public TaskStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Private data directory is required");
        var full = Path.GetFullPath(directory);
        EnsurePrivateDirectory(full);
        _databasePath = Path.Combine(full, "codingassistant.db");
        if (File.Exists(_databasePath) &&
            (File.GetAttributes(_databasePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing symlinked SQLite database");

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
        RestrictDatabasePermissions();
    }

    private static bool Unix =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD();

    private static void EnsurePrivateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing symlinked data directory");
        }
        else if (Unix)
        {
            Directory.CreateDirectory(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            Directory.CreateDirectory(path);
        }

        if (Unix)
        {
            var allowed = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                          UnixFileMode.UserExecute;
            if ((File.GetUnixFileMode(path) & ~allowed) != 0)
                throw new InvalidOperationException("Data directory must be owner-private");
        }
    }

    private void RestrictDatabasePermissions()
    {
        if (!Unix)
            return;
        var privateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(_databasePath, privateFile);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        Execute(db, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        return db;
    }

    private static SqliteCommand Command(
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

    private static int Execute(
        SqliteConnection db, SqliteTransaction? tx, string statement,
        params (string Key, object Value)[] values)
    {
        using var command = Command(db, tx, statement, values);
        return command.ExecuteNonQuery();
    }

    private static string? Scalar(
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

    private static TaskItem? FindTask(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        using var cmd = Command(db, tx, """
            SELECT id,title,objective,status,current_version,created_utc
            FROM tasks WHERE id=@id
            """, ("@id", id));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadTask(reader) : null;
    }

    private static PlanItem? FindPlan(
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
