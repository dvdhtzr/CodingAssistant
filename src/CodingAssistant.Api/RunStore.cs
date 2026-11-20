using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

/// <summary>
/// Operator intent to work on an already-approved, immutable planning record.
/// Intent is NOT an execution authorization or worker queue entry.
/// </summary>
public sealed record CreateRunIntentRequest(int PlanVersion, string? PlanHash, string? RequestId);
public sealed record RunItem(
    string Id, string TaskId, int PlanVersion, string PlanHash, string State,
    bool PlanCurrent, bool ExecutionEnabled, string CreatedUtc, string UpdatedUtc,
    long LastSequence);
public sealed record RunEvent(
    string RunId, long Sequence, string Kind, string State, string CreatedUtc);
public sealed record RunEventPage(
    IReadOnlyList<RunEvent> Events, long NextCursor);
public sealed record RunChange(RunItem Run, RunEvent? NewEvent);

/// <summary>
/// SQLite is the truth for both run intent and its event sequence.
/// SignalR is an optional, lossy notification channel; clients replay events
/// from this store after subscribing or reconnecting.
/// </summary>
public sealed class RunStore(TaskStore tasks)
{
    private static readonly Regex Id32 = new(
        "^[0-9a-f]{32}\\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Hash64 = new(
        "^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS run_intents (
              id TEXT NOT NULL PRIMARY KEY,
              task_id TEXT NOT NULL REFERENCES tasks(id),
              plan_version INTEGER NOT NULL,
              plan_hash TEXT NOT NULL,
              request_id TEXT NOT NULL UNIQUE,
              state TEXT NOT NULL CHECK(state IN
                  ('AwaitingExecutionAuthorization','Cancelled')),
              created_utc TEXT NOT NULL,
              updated_utc TEXT NOT NULL,
              last_sequence INTEGER NOT NULL CHECK(last_sequence >= 1),
              FOREIGN KEY(task_id,plan_version) REFERENCES plans(task_id,version)
            );
            CREATE TABLE IF NOT EXISTS run_events (
              run_id TEXT NOT NULL REFERENCES run_intents(id),
              sequence INTEGER NOT NULL CHECK(sequence > 0),
              kind TEXT NOT NULL CHECK(kind IN
                  ('IntentCreated','IntentCancelled')),
              state TEXT NOT NULL CHECK(state IN
                  ('AwaitingExecutionAuthorization','Cancelled')),
              created_utc TEXT NOT NULL,
              PRIMARY KEY(run_id,sequence)
            );
            CREATE INDEX IF NOT EXISTS ix_run_intents_task_created
              ON run_intents(task_id,created_utc DESC,id DESC);
            """);
    }

    private static string ValidId(string? value)
    {
        if (value is null || !Id32.IsMatch(value))
            throw new StoreFailure("INVALID_ID");
        return value;
    }

    private static RunItem ReadRun(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt32(5) != 0,
        ExecutionEnabled: false,
        reader.GetString(6),
        reader.GetString(7),
        reader.GetInt64(8));

    private const string SelectRun = """
        SELECT r.id,r.task_id,r.plan_version,r.plan_hash,r.state,
          CASE WHEN r.plan_version=t.current_version AND
            p.status='Approved' THEN 1 ELSE 0 END,
          r.created_utc,r.updated_utc,r.last_sequence
        FROM run_intents r
        JOIN tasks t ON t.id=r.task_id
        JOIN plans p ON p.task_id=r.task_id AND p.version=r.plan_version
        """;

    private static RunItem? FindRun(
        SqliteConnection db, SqliteTransaction? tx, string runId)
    {
        using var command = TaskStore.Command(
            db, tx, SelectRun + " WHERE r.id=@id", ("@id", runId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    public RunItem Get(string runId)
    {
        ValidId(runId);
        using var db = tasks.Open();
        return FindRun(db, null, runId) ?? throw new StoreFailure("NOT_FOUND");
    }

    public IReadOnlyList<RunItem> ListForTask(string taskId, int limit = 50)
    {
        ValidId(taskId);
        if (limit is < 1 or > 100)
            throw new StoreFailure("INVALID_LIMIT");
        using var db = tasks.Open();
        if (TaskStore.FindTask(db, null, taskId) is null)
            throw new StoreFailure("NOT_FOUND");
        using var command = TaskStore.Command(db, null,
            SelectRun + " WHERE r.task_id=@task " +
            "ORDER BY r.created_utc DESC,r.id DESC LIMIT @limit",
            ("@task", taskId), ("@limit", limit));
        using var reader = command.ExecuteReader();
        var result = new List<RunItem>();
        while (reader.Read())
            result.Add(ReadRun(reader));
        return result;
    }

    public RunChange CreateIntent(string taskId, CreateRunIntentRequest request)
    {
        ValidId(taskId);
        if (request.RequestId is null || !Id32.IsMatch(request.RequestId))
            throw new StoreFailure("INVALID_REQUEST_ID");
        if (request.PlanHash is null || !Hash64.IsMatch(request.PlanHash))
            throw new StoreFailure("INVALID_PLAN_HASH");
        if (request.PlanVersion < 1)
            throw new StoreFailure("INVALID_VERSION");

        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var priorId = TaskStore.Scalar(db, tx,
            "SELECT id FROM run_intents WHERE request_id=@request",
            ("@request", request.RequestId));
        if (priorId is not null)
        {
            var prior = FindRun(db, tx, priorId)!;
            if (prior.TaskId != taskId ||
                prior.PlanVersion != request.PlanVersion ||
                prior.PlanHash != request.PlanHash)
                throw new StoreFailure("IDEMPOTENCY_CONFLICT");
            tx.Commit();
            return new RunChange(prior, null);
        }

        var task = TaskStore.FindTask(db, tx, taskId) ??
                   throw new StoreFailure("NOT_FOUND");
        if (task.CurrentVersion != request.PlanVersion)
            throw new StoreFailure("STALE_VERSION");
        var plan = TaskStore.FindPlan(db, tx, taskId, request.PlanVersion) ??
                   throw new StoreFailure("NOT_FOUND");
        if (plan.Status != "Approved" || plan.PlanHash != request.PlanHash)
            throw new StoreFailure("PLAN_NOT_APPROVED");

        var id = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        const string state = "AwaitingExecutionAuthorization";
        TaskStore.Execute(db, tx, """
            INSERT INTO run_intents
            (id,task_id,plan_version,plan_hash,request_id,state,
             created_utc,updated_utc,last_sequence)
            VALUES (@id,@task,@version,@hash,@request,@state,@utc,@utc,1)
            """,
            ("@id", id), ("@task", taskId),
            ("@version", request.PlanVersion),
            ("@hash", request.PlanHash), ("@request", request.RequestId),
            ("@state", state), ("@utc", timestamp));
        TaskStore.Execute(db, tx, """
            INSERT INTO run_events
            (run_id,sequence,kind,state,created_utc)
            VALUES (@id,1,'IntentCreated',@state,@utc)
            """, ("@id", id), ("@state", state), ("@utc", timestamp));
        var item = FindRun(db, tx, id)!;
        tx.Commit();
        return new RunChange(item,
            new RunEvent(id, 1, "IntentCreated", state, timestamp));
    }

    public RunChange CancelIntent(string runId)
    {
        ValidId(runId);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var current = FindRun(db, tx, runId) ?? throw new StoreFailure("NOT_FOUND");
        if (current.State == "Cancelled")
        {
            tx.Commit();
            return new RunChange(current, null);
        }
        if (current.State != "AwaitingExecutionAuthorization")
            throw new StoreFailure("INVALID_STATE");
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        var sequence = checked(current.LastSequence + 1);
        TaskStore.Execute(db, tx, """
            UPDATE run_intents SET state='Cancelled',
               updated_utc=@utc,last_sequence=@seq
            WHERE id=@id AND state='AwaitingExecutionAuthorization'
            """, ("@id", runId), ("@utc", timestamp), ("@seq", sequence));
        TaskStore.Execute(db, tx, """
            INSERT INTO run_events
            (run_id,sequence,kind,state,created_utc)
            VALUES (@id,@seq,'IntentCancelled','Cancelled',@utc)
            """, ("@id", runId), ("@seq", sequence), ("@utc", timestamp));
        var result = FindRun(db, tx, runId)!;
        tx.Commit();
        return new RunChange(result, new RunEvent(
            runId, sequence, "IntentCancelled", "Cancelled", timestamp));
    }

    public RunEventPage ReadEvents(string runId, long after = 0, int limit = 50)
    {
        ValidId(runId);
        if (after < 0 || limit is < 1 or > 100)
            throw new StoreFailure("INVALID_CURSOR");
        using var db = tasks.Open();
        // Query does not change state and can replay after a lost SignalR
        // notification, host restart, or temporary browser disconnection.
        if (FindRun(db, null, runId) is null)
            throw new StoreFailure("NOT_FOUND");
        using var command = TaskStore.Command(db, null, """
            SELECT run_id,sequence,kind,state,created_utc
            FROM run_events WHERE run_id=@id AND sequence>@after
            ORDER BY sequence ASC LIMIT @limit
            """, ("@id", runId), ("@after", after), ("@limit", limit));
        using var reader = command.ExecuteReader();
        var events = new List<RunEvent>();
        while (reader.Read())
            events.Add(new RunEvent(
                reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4)));
        var cursor = events.Count == 0 ? after : events[^1].Sequence;
        return new RunEventPage(events, cursor);
    }
}
