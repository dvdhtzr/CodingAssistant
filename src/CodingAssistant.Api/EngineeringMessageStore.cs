using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record EngineeringMessage(long Sequence, string TaskId,
    string RunId, string WorkItemId, string Kind, string FromAgent,
    string ToAgent, int PlanRevision, string PlanSha256,
    int SpecificationVersion, string SpecificationSha256,
    string ArchitectureSha256, string SourceSha256, string CreatedUtc);

/// <summary>Durable host-observed communication receipts. The first two
/// message kinds are persisted atomically with existing assignment transitions.
/// Receipts are not permissions, executable commands or a model inbox yet.</summary>
public sealed class EngineeringMessageStore(TaskStore tasks)
{
    internal static void Initialize(SqliteConnection db) =>
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS engineering_messages (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                run_id TEXT NOT NULL,
                work_item_id TEXT NOT NULL,
                kind TEXT NOT NULL CHECK(kind IN ('TaskAssignment','TaskCompleted')),
                from_agent TEXT NOT NULL,
                to_agent TEXT NOT NULL,
                plan_revision INTEGER NOT NULL,
                plan_sha256 TEXT NOT NULL,
                spec_version INTEGER NOT NULL,
                spec_sha256 TEXT NOT NULL,
                architecture_sha256 TEXT NOT NULL,
                source_sha256 TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                UNIQUE(run_id, work_item_id, kind)
            );
            CREATE INDEX IF NOT EXISTS engineering_messages_task_sequence
                ON engineering_messages(task_id, sequence);
            """);

    internal static void Record(SqliteConnection db, SqliteTransaction tx,
        EngineeringRun job, string workItemId, bool completed)
    {
        // These identities are determined by the trusted host transition,
        // never accepted from a model response or an HTTP caller.
        var from = completed ? "SoftwareEngineer" : "TechnicalLead";
        var to = completed ? "TechnicalLead" : "SoftwareEngineer";
        EngineeringRoleCapabilities.Require(from, completed
            ? EngineeringCapability.WriteSource : EngineeringCapability.ScheduleWork);
        EngineeringRoleCapabilities.Require(from, EngineeringCapability.SendMessage);
        EngineeringRunStore.RequireBinding(db, tx, job);
        var sourceSha = SourceFingerprint(db, tx, job.Id);
        TaskStore.Execute(db, tx, """
            INSERT INTO engineering_messages(task_id,run_id,work_item_id,kind,
                from_agent,to_agent,plan_revision,plan_sha256,spec_version,
                spec_sha256,architecture_sha256,source_sha256,created_utc)
            VALUES(@task,@run,@item,@kind,@from,@to,@revision,@plan,@version,
                @spec,@arch,@source,@utc)
            """, ("@task", job.TaskId), ("@run", job.Id), ("@item", workItemId),
            ("@kind", completed ? "TaskCompleted" : "TaskAssignment"),
            ("@from", from), ("@to", to), ("@revision", job.PlanRevision),
            ("@plan", job.PlanSha256), ("@version", job.SpecificationVersion),
            ("@spec", job.SpecificationSha256), ("@arch", job.ArchitectureSha256),
            ("@source", sourceSha), ("@utc", DateTimeOffset.UtcNow.ToString("O")));
    }

    internal static string SourceFingerprint(SqliteConnection db, SqliteTransaction tx, string runId)
    {
        var hashes = new List<string[]>();
        using (var command = TaskStore.Command(db, tx, """
            SELECT path,sha256 FROM engineering_run_files
            WHERE run_id=@run ORDER BY path COLLATE BINARY
            """, ("@run", runId)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                hashes.Add([reader.GetString(0), reader.GetString(1)]);
        // Use the same path/hash canonicalization as independent QA.
        // The empty assignment snapshot is hashed, not claimed to be reviewed.
        var input = string.Join("\n", hashes.OrderBy(f => f[0],
            StringComparer.OrdinalIgnoreCase).Select(f => f[0] + "\t" + f[1]));
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    public IReadOnlyList<EngineeringMessage> List(string taskId, long after = 0)
    {
        if (after < 0)
            throw new StoreFailure("INVALID_INPUT");
        using var db = tasks.Open();
        if (TaskStore.FindTask(db, null, taskId) is null)
            throw new StoreFailure("NOT_FOUND");
        using var command = TaskStore.Command(db, null, """
            SELECT sequence,task_id,run_id,work_item_id,kind,from_agent,to_agent,
                plan_revision,plan_sha256,spec_version,spec_sha256,
                architecture_sha256,source_sha256,created_utc
            FROM engineering_messages WHERE task_id=@task AND sequence>@after
            ORDER BY sequence LIMIT 200
            """, ("@task", taskId), ("@after", after));
        using var reader = command.ExecuteReader();
        var result = new List<EngineeringMessage>();
        while (reader.Read())
            result.Add(new(reader.GetInt64(0),reader.GetString(1),
                reader.GetString(2),reader.GetString(3),reader.GetString(4),
                reader.GetString(5),reader.GetString(6),reader.GetInt32(7),
                reader.GetString(8),reader.GetInt32(9),reader.GetString(10),
                reader.GetString(11),reader.GetString(12),reader.GetString(13)));
        return result.AsReadOnly();
    }
}
