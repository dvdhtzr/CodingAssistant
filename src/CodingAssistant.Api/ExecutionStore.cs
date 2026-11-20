using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record AuthorizeExecutionRequest(string? Approval);
public sealed record ExecutionPlanView(
    string RunId, string PlanHash, JsonElement Plan, string State,
    string CreatedUtc, string UpdatedUtc);
public sealed record ExecutionChange(ExecutionPlanView Plan, RunEvent? NewEvent);

/// <summary>
/// Durable M1 executable authorization is independent of M2 planning approval.
/// The existing run-intent schema is preserved; execution history has its own
/// transactional event table with the SAME monotonically increasing sequence.
/// No model or process is started by this storage class.
/// </summary>
public sealed class ExecutionStore(TaskStore tasks)
{
    private static readonly Regex Id32 =
        new("^[0-9a-f]{32}\\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Hash64 =
        new("^[0-9a-f]{64}\\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string Previewed = "Previewed";

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS run_executions (
                run_id TEXT NOT NULL PRIMARY KEY REFERENCES run_intents(id),
                execution_hash TEXT NOT NULL,
                plan_json TEXT NOT NULL,
                state TEXT NOT NULL CHECK (state IN (
                    'Previewed','Authorized','Starting','Running','Cancelling',
                    'Succeeded','Failed','Cancelled','RecoveryBlocked','Interrupted')),
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS execution_events (
                run_id TEXT NOT NULL REFERENCES run_intents(id),
                sequence INTEGER NOT NULL CHECK(sequence > 0),
                kind TEXT NOT NULL,
                state TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(run_id,sequence)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_single_active_execution
              ON run_executions ((1))
              WHERE state IN ('Starting','Running','Cancelling');
            """);
    }

    public static void CheckId(string? id)
    {
        if (id is null || !Id32.IsMatch(id))
            throw new StoreFailure("INVALID_ID");
    }

    private static ExecutionPlanView Read(SqliteDataReader r)
    {
        using var doc = JsonDocument.Parse(r.GetString(2));
        return new ExecutionPlanView(r.GetString(0), r.GetString(1),
            doc.RootElement.Clone(), r.GetString(3),
            r.GetString(4), r.GetString(5));
    }

    private static ExecutionPlanView? Find(SqliteConnection db,
        SqliteTransaction? tx, string runId)
    {
        using var query = TaskStore.Command(db, tx, """
            SELECT run_id,execution_hash,plan_json,state,created_utc,updated_utc
            FROM run_executions WHERE run_id=@id
            """, ("@id", runId));
        using var reader = query.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public ExecutionPlanView Get(string runId)
    {
        CheckId(runId);
        using var db = tasks.Open();
        return Find(db, null, runId) ?? throw new StoreFailure("NOT_FOUND");
    }

    private static void RequireCurrent(SqliteConnection db, SqliteTransaction tx,
        string runId)
    {
        var valid = TaskStore.Scalar(db, tx, """
            SELECT r.id FROM run_intents r
            JOIN tasks t ON t.id=r.task_id
            JOIN plans p ON p.task_id=r.task_id AND p.version=r.plan_version
            WHERE r.id=@id AND r.state='AwaitingExecutionAuthorization'
              AND t.current_version=r.plan_version
              AND p.plan_hash=r.plan_hash AND p.status='Approved'
            """, ("@id", runId));
        if (valid is null)
            throw new StoreFailure("PLAN_NOT_APPROVED");
    }

    private static RunEvent Append(SqliteConnection db, SqliteTransaction tx,
        string runId, string kind, string state, string utc)
    {
        var next = long.Parse(TaskStore.Scalar(db, tx,
            "SELECT CAST(last_sequence + 1 AS TEXT) FROM run_intents WHERE id=@id",
            ("@id", runId)) ?? throw new StoreFailure("NOT_FOUND"),
            System.Globalization.CultureInfo.InvariantCulture);
        if (TaskStore.Execute(db, tx, """
            UPDATE run_intents SET last_sequence=@seq,updated_utc=@utc
            WHERE id=@id AND last_sequence=@previous
            """, ("@seq", next), ("@utc", utc),
                ("@id", runId), ("@previous", next - 1)) != 1)
            throw new StoreFailure("STALE_VERSION");
        TaskStore.Execute(db, tx, """
            INSERT INTO execution_events(run_id,sequence,kind,state,created_utc)
            VALUES(@id,@seq,@kind,@state,@utc)
            """, ("@id", runId), ("@seq", next),
                ("@kind", kind), ("@state", state), ("@utc", utc));
        return new RunEvent(runId, next, kind, state, utc);
    }

    public ExecutionChange RecordPreview(string runId, string hash, JsonElement plan)
    {
        CheckId(runId);
        if (!Hash64.IsMatch(hash) || plan.ValueKind != JsonValueKind.Object)
            throw new StoreFailure("INVALID_EXECUTION_PLAN");
        var json = plan.GetRawText();
        if (json.Length is < 100 or > 12000)
            throw new StoreFailure("INVALID_EXECUTION_PLAN");
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        RequireCurrent(db, tx, runId);
        var previous = Find(db, tx, runId);
        if (previous is not null)
        {
            if (previous.PlanHash != hash || previous.Plan.GetRawText() != json)
                throw new StoreFailure("EXECUTION_PLAN_CONFLICT");
            tx.Commit();
            return new ExecutionChange(previous, null);
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        TaskStore.Execute(db, tx, """
            INSERT INTO run_executions
            (run_id,execution_hash,plan_json,state,created_utc,updated_utc)
            VALUES(@id,@hash,@json,'Previewed',@utc,@utc)
            """, ("@id", runId), ("@hash", hash),
                ("@json", json), ("@utc", now));
        var ev = Append(db, tx, runId, "ExecutionPlanPrepared", Previewed, now);
        var result = Find(db, tx, runId)!;
        tx.Commit();
        return new ExecutionChange(result, ev);
    }

    public ExecutionChange Authorize(string runId, AuthorizeExecutionRequest input)
    {
        CheckId(runId);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        RequireCurrent(db, tx, runId);
        var current = Find(db, tx, runId) ?? throw new StoreFailure("NOT_FOUND");
        if (!string.Equals(input.Approval, "APPROVE " + current.PlanHash,
                           StringComparison.Ordinal))
            throw new StoreFailure("APPROVAL_MISMATCH");
        if (current.State == "Authorized")
        {
            tx.Commit();
            return new ExecutionChange(current, null);
        }
        if (current.State != Previewed)
            throw new StoreFailure("INVALID_STATE");
        var result = Transition(db, tx, runId, Previewed, "Authorized",
                                "ExecutionAuthorized");
        tx.Commit();
        return result;
    }

    private static ExecutionChange Transition(SqliteConnection db,
        SqliteTransaction tx, string runId, string from, string to, string kind)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        if (TaskStore.Execute(db, tx, """
            UPDATE run_executions SET state=@to,updated_utc=@utc
            WHERE run_id=@id AND state=@from
            """, ("@to", to), ("@utc", now),
                ("@id", runId), ("@from", from)) != 1)
            throw new StoreFailure("INVALID_STATE");
        var ev = Append(db, tx, runId, kind, to, now);
        return new ExecutionChange(Find(db, tx, runId)!, ev);
    }

    public ExecutionChange Admit(string runId)
    {
        CheckId(runId);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        RequireCurrent(db, tx, runId);
        var current = Find(db, tx, runId) ?? throw new StoreFailure("NOT_FOUND");
        if (current.State != "Authorized")
            throw new StoreFailure("INVALID_STATE");
        // At most one active M1 worker per host DB, independently protected by
        // ix_single_active_execution against simultaneous API processes.
        // An unresolved orphan may still own a live labeled container.
        // Never admit another worker while ANY cleanup is uncertain.
        if (TaskStore.Scalar(db, tx, """
            SELECT run_id FROM run_executions
            WHERE state IN ('Starting','Running','Cancelling','RecoveryBlocked')
            LIMIT 1
            """) is not null)
            throw new StoreFailure("EXECUTION_BUSY");
        var changed = Transition(db, tx, runId, "Authorized", "Starting",
                                 "ExecutionAdmitted");
        tx.Commit();
        return changed;
    }

    public ExecutionChange Move(string runId, string from, string to, string kind)
    {
        CheckId(runId);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var change = Transition(db, tx, runId, from, to, kind);
        tx.Commit();
        return change;
    }

    public ExecutionChange CancelBeforeStart(string runId)
    {
        CheckId(runId);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var current = Find(db, tx, runId) ?? throw new StoreFailure("NOT_FOUND");
        if (current.State == "Cancelled")
        {
            tx.Commit();
            return new ExecutionChange(current, null);
        }
        if (current.State is not ("Previewed" or "Authorized"))
            throw new StoreFailure("INVALID_STATE");
        var change = Transition(db, tx, runId, current.State, "Cancelled",
                                "ExecutionCancelled");
        tx.Commit();
        return change;
    }

    public void MarkUnreconciledOnStartup()
    {
        // No restart may silently resume work. Manual model-free reconciliation
        // is required before re-enabling the execution boundary.
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        using var query = TaskStore.Command(db, tx, """
            SELECT run_id,state FROM run_executions
            WHERE state IN ('Starting','Running','Cancelling')
            """);
        using var reader = query.ExecuteReader();
        var active = new List<(string Id, string State)>();
        while (reader.Read())
            active.Add((reader.GetString(0), reader.GetString(1)));
        reader.Close();
        foreach (var (id, state) in active)
            Transition(db, tx, id, state, "RecoveryBlocked",
                       "ExecutionRecoveryRequired");
        tx.Commit();
    }
}
