using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

// This is the durable task-specific build admission boundary, NOT the
// historical M1.12 fixed-fixture executor. A scope acknowledgement is NOT
// an executable M1 plan approval. The independent runtime fingerprint and
// issue #76 security qualification must be completed before Start is enabled.
public sealed record PrepareTaskBuildRequest(string? RequestId);
public sealed record AcknowledgeTaskBuildRequest(string? Approval);
public sealed record TaskBuildView(
    string Id, string TaskId, string ContractSha256, JsonElement Contract,
    string State, bool ExecutionEnabled, string Blocker,
    string CreatedUtc, string UpdatedUtc);
public sealed record TaskBuildEvent(int Sequence, string Kind, string CreatedUtc);
public sealed record TaskBuildSnapshot(TaskBuildView Build,
                                      IReadOnlyList<TaskBuildEvent> Events);
public sealed record TaskBuildEventPage(IReadOnlyList<TaskBuildEvent> Events,
                                        int NextCursor);

public sealed class TaskBuildStore(TaskStore tasks)
{
    private static readonly Regex Id = new("^[0-9a-f]{32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string Blocker = "INTEGRATED_SECURITY_QUALIFICATION_PENDING";
    private const string Select = """
        SELECT id,task_id,contract_sha,contract_json,state,created_utc,updated_utc
          FROM task_builds
        """;

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS task_builds (
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                request_id TEXT NOT NULL UNIQUE,
                contract_sha TEXT NOT NULL,
                contract_json TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('Prepared','ScopeAcknowledged','Cancelled')),
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_task_builds_recent
                ON task_builds(task_id,created_utc DESC,id DESC);
            CREATE TABLE IF NOT EXISTS task_build_events (
                build_id TEXT NOT NULL REFERENCES task_builds(id),
                sequence INTEGER NOT NULL CHECK(sequence>0),
                kind TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(build_id,sequence)
            );
            """);
    }

    private static void ValidId(string? id)
    {
        if (id is null || !Id.IsMatch(id))
            throw new StoreFailure("INVALID_ID");
    }

    private static TaskBuildView Read(SqliteDataReader reader)
    {
        using var json = JsonDocument.Parse(reader.GetString(3));
        return new TaskBuildView(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), json.RootElement.Clone(), reader.GetString(4),
            false, Blocker, reader.GetString(5), reader.GetString(6));
    }

    private static TaskBuildView? Find(SqliteConnection db,
        SqliteTransaction? tx, string buildId)
    {
        using var cmd = TaskStore.Command(db, tx,
            Select + " WHERE id=@id", ("@id", buildId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static TaskBuildView? LatestBuild(SqliteConnection db,
        string taskId)
    {
        using var cmd = TaskStore.Command(db, null,
            Select + " WHERE task_id=@task ORDER BY created_utc DESC,id DESC LIMIT 1",
            ("@task", taskId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static void RequireCurrent(SqliteConnection db, SqliteTransaction tx,
                                       TaskBuildView build)
    {
        // Read from the SAME transaction as a state change. No stale, revoked,
        // or replaced interview spec/file plan may be acknowledged or admitted.
        var contract = build.Contract;
        var taskId = build.TaskId;
        if (TaskStore.Scalar(db, tx,
                "SELECT objective FROM tasks WHERE id=@task",
                ("@task", taskId)) != contract.GetProperty("objective").GetString())
            throw new StoreFailure("SPECIFICATION_CONFLICT");

        using (var cmd = TaskStore.Command(db, tx, """
            SELECT version,sha256,model,content,status FROM interview_specs
            WHERE task_id=@task ORDER BY version DESC LIMIT 1
            """, ("@task", taskId)))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read() ||
                reader.GetInt32(0) != contract.GetProperty("specificationVersion").GetInt32() ||
                reader.GetString(1) != contract.GetProperty("approvedSpecificationSha256").GetString() ||
                reader.GetString(2) != contract.GetProperty("model").GetString() ||
                reader.GetString(3) != contract.GetProperty("specification").GetString() ||
                reader.GetString(4) != "Approved")
                throw new StoreFailure("SPECIFICATION_CONFLICT");
        }

        string draftId;
        using (var cmd = TaskStore.Command(db, tx, """
            SELECT id,spec_version,spec_sha256,model,state FROM project_drafts
            WHERE task_id=@task ORDER BY created_utc DESC,id DESC LIMIT 1
            """, ("@task", taskId)))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read() ||
                reader.GetInt32(1) != contract.GetProperty("specificationVersion").GetInt32() ||
                reader.GetString(2) != contract.GetProperty("approvedSpecificationSha256").GetString() ||
                reader.GetString(3) != contract.GetProperty("model").GetString() ||
                reader.GetString(4) != "Generated")
                throw new StoreFailure("SPECIFICATION_CONFLICT");
            draftId = reader.GetString(0);
        }

        var actual = new List<string>();
        using (var cmd = TaskStore.Command(db, tx,
                   "SELECT path FROM project_draft_files WHERE draft_id=@draft ORDER BY path",
                   ("@draft", draftId)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                actual.Add(reader.GetString(0));
        var allowed = contract.GetProperty("files").EnumerateArray()
            .Select(value => value.GetString()!).OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        if (!actual.OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(
                allowed, StringComparer.Ordinal))
            throw new StoreFailure("SPECIFICATION_CONFLICT");
    }

    private static void Append(SqliteConnection db, SqliteTransaction tx,
                               string buildId, string kind, string utc)
    {
        var next = int.Parse(TaskStore.Scalar(db, tx, """
            SELECT CAST(COALESCE(MAX(sequence),0)+1 AS TEXT)
              FROM task_build_events WHERE build_id=@id
            """, ("@id", buildId))!);
        TaskStore.Execute(db, tx, """
            INSERT INTO task_build_events(build_id,sequence,kind,created_utc)
            VALUES(@id,@sequence,@kind,@utc)
            """, ("@id", buildId), ("@sequence", next),
                ("@kind", kind), ("@utc", utc));
    }

    public TaskBuildSnapshot? Latest(string taskId)
    {
        ValidId(taskId);
        using var db = tasks.Open();
        if (TaskStore.FindTask(db, null, taskId) is null)
            throw new StoreFailure("NOT_FOUND");
        var build = LatestBuild(db, taskId);
        return build is null ? null : Snapshot(db, build);
    }

    public TaskBuildSnapshot Get(string buildId)
    {
        ValidId(buildId);
        using var db = tasks.Open();
        var build = Find(db, null, buildId) ?? throw new StoreFailure("NOT_FOUND");
        return Snapshot(db, build);
    }

    private static TaskBuildSnapshot Snapshot(SqliteConnection db, TaskBuildView build)
    {
        using var command = TaskStore.Command(db, null, """
            SELECT sequence,kind,created_utc FROM task_build_events
            WHERE build_id=@id ORDER BY sequence
            """, ("@id", build.Id));
        using var reader = command.ExecuteReader();
        var events = new List<TaskBuildEvent>();
        while (reader.Read())
            events.Add(new TaskBuildEvent(reader.GetInt32(0), reader.GetString(1),
                                          reader.GetString(2)));
        return new TaskBuildSnapshot(build, events);
    }

    public TaskBuildSnapshot Prepare(string taskId, string? requestId,
                                     TaskAgentContractPreview preview)
    {
        ValidId(taskId);
        ValidId(requestId);
        if (preview.ExecutionEnabled || preview.Contract.ValueKind != JsonValueKind.Object)
            throw new StoreFailure("INVALID_EXECUTION_PLAN");

        // Exact support envelope of the existing isolated task pilot. A React
        // or HTML-only draft is explicitly NOT treated as a qualified .NET build.
        var files = preview.Contract.GetProperty("files").EnumerateArray()
            .Select(x => x.GetString()!).ToArray();
        if (files.Length is < 1 or > 5 ||
            !files.Any(x => x.EndsWith(".csproj", StringComparison.Ordinal)) ||
            files.Any(x => !(x.EndsWith(".csproj", StringComparison.Ordinal) ||
                             x.EndsWith(".cs", StringComparison.Ordinal))))
            throw new StoreFailure("TASK_PROFILE_UNSUPPORTED");

        var json = preview.Contract.GetRawText();
        var computedSha = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        if (json.Length > 24576 ||
            !string.Equals(computedSha, preview.ContractSha256, StringComparison.Ordinal) ||
            preview.Contract.GetProperty("schema").GetString() != TaskAgentContract.Schema ||
            preview.Contract.GetProperty("taskId").GetString() != taskId ||
            preview.Contract.GetProperty("model").GetString() != TaskStore.Model)
            throw new StoreFailure("INVALID_EXECUTION_PLAN");
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var existing = TaskStore.Scalar(db, tx,
            "SELECT id FROM task_builds WHERE request_id=@request",
            ("@request", requestId!));
        if (existing is not null)
        {
            var previous = Find(db, tx, existing)!;
            if (previous.TaskId != taskId ||
                previous.ContractSha256 != preview.ContractSha256 ||
                previous.Contract.GetRawText() != json)
                throw new StoreFailure("IDEMPOTENCY_CONFLICT");
            tx.Commit();
            return Snapshot(db, previous);
        }
        var id = Guid.NewGuid().ToString("N");
        var utc = DateTimeOffset.UtcNow.ToString("O");
        var current = new TaskBuildView(id, taskId, preview.ContractSha256,
            preview.Contract, "Prepared", false, Blocker, utc, utc);
        RequireCurrent(db, tx, current);
        TaskStore.Execute(db, tx, """
            INSERT INTO task_builds
            (id,task_id,request_id,contract_sha,contract_json,state,created_utc,updated_utc)
            VALUES(@id,@task,@request,@sha,@json,'Prepared',@utc,@utc)
            """, ("@id", id), ("@task", taskId), ("@request", requestId!),
                ("@sha", preview.ContractSha256), ("@json", json), ("@utc", utc));
        Append(db, tx, id, "TaskScopePrepared", utc);
        tx.Commit();
        return Get(id);
    }

    public TaskBuildSnapshot Acknowledge(string buildId, string? approval)
    {
        ValidId(buildId);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var current = Find(db, tx, buildId) ?? throw new StoreFailure("NOT_FOUND");
        RequireCurrent(db, tx, current);
        if (approval != "APPROVE " + current.ContractSha256)
            throw new StoreFailure("APPROVAL_MISMATCH");
        if (current.State is not ("Prepared" or "ScopeAcknowledged"))
            throw new StoreFailure("INVALID_STATE");
        if (current.State == "Prepared")
        {
            var utc = DateTimeOffset.UtcNow.ToString("O");
            TaskStore.Execute(db, tx, """
                UPDATE task_builds SET state='ScopeAcknowledged',updated_utc=@utc
                WHERE id=@id AND state='Prepared'
                """, ("@id", buildId), ("@utc", utc));
            Append(db, tx, buildId, "TaskScopeAcknowledged", utc);
        }
        tx.Commit();
        return Get(buildId);
    }

    public TaskBuildSnapshot Cancel(string buildId)
    {
        ValidId(buildId);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var current = Find(db, tx, buildId) ?? throw new StoreFailure("NOT_FOUND");
        if (current.State != "Cancelled")
        {
            var utc = DateTimeOffset.UtcNow.ToString("O");
            TaskStore.Execute(db, tx, """
                UPDATE task_builds SET state='Cancelled',updated_utc=@utc
                WHERE id=@id AND state IN ('Prepared','ScopeAcknowledged')
                """, ("@id", buildId), ("@utc", utc));
            Append(db, tx, buildId, "TaskScopeCancelled", utc);
        }
        tx.Commit();
        return Get(buildId);
    }

    public void RequireStartGate(string buildId)
    {
        ValidId(buildId);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var current = Find(db, tx, buildId) ?? throw new StoreFailure("NOT_FOUND");
        if (current.State != "ScopeAcknowledged")
            throw new StoreFailure("INVALID_STATE");
        RequireCurrent(db, tx, current);
        // No executable fingerprint, OS qualification or real run attached yet.
        // Never route a user task through the legacy fixed-fixture M1 runner.
        throw new ExecutionUnavailable();
    }

    public TaskBuildEventPage ReadEvents(string buildId, int after = 0,
                                         int limit = 50)
    {
        ValidId(buildId);
        if (after < 0 || limit is < 1 or > 100)
            throw new StoreFailure("INVALID_INPUT");
        using var db = tasks.Open();
        _ = Find(db, null, buildId) ?? throw new StoreFailure("NOT_FOUND");
        using var cmd = TaskStore.Command(db, null, """
            SELECT sequence,kind,created_utc FROM task_build_events
            WHERE build_id=@id AND sequence>@after
            ORDER BY sequence LIMIT @limit
            """, ("@id", buildId), ("@after", after), ("@limit", limit));
        using var reader = cmd.ExecuteReader();
        var events = new List<TaskBuildEvent>();
        while (reader.Read())
            events.Add(new TaskBuildEvent(reader.GetInt32(0), reader.GetString(1),
                                          reader.GetString(2)));
        return new TaskBuildEventPage(events,
            events.Count == 0 ? after : events[^1].Sequence);
    }
}
