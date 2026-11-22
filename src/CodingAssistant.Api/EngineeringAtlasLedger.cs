using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

/// <summary>
/// A content-derived, append-only notification about one committed engineering
/// work item. It contains file paths and hashes, never source contents.
/// </summary>
public sealed record AtlasSourceChange(string Path, string Kind, string Sha256);
public sealed record AtlasSourceEvent(
    string RunId, int Sequence, string WorkItemId, string SourceRevision,
    IReadOnlyList<AtlasSourceChange> Changes, string CreatedUtc);
public sealed record AtlasSourceEventPage(
    string RunId, int LastSequence, IReadOnlyList<AtlasSourceEvent> Events);

/// <summary>
/// The immutable source-change ledger is written inside EngineeringRunStore's
/// approved-source transaction. It is not a separate agent, workspace, or
/// source execution capability.
/// </summary>
public sealed class EngineeringAtlasLedger(TaskStore tasks, EngineeringRunStore engineering)
{
    private const int PageSize = 50;

    public void Initialize()
    {
        using var db = tasks.Open();
        EnsureSchema(db);
    }

    // Must also be initialized by EngineeringRunStore for direct unit-test
    // instances that bypass ApiHost dependency registration.
    internal static void EnsureSchema(SqliteConnection db)
    {
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS engineering_atlas_events (
                run_id TEXT NOT NULL REFERENCES engineering_runs(id),
                sequence INTEGER NOT NULL CHECK(sequence > 0),
                work_item_id TEXT NOT NULL,
                source_revision TEXT NOT NULL,
                changes_json TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(run_id,sequence)
            );
            """);
    }

    private static string Sha(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
            .ToLowerInvariant();

    public static Dictionary<string, string> CaptureHashes(
        SqliteConnection db, SqliteTransaction tx, string runId)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = TaskStore.Command(db, tx, """
            SELECT path,sha256 FROM engineering_run_files
            WHERE run_id=@run ORDER BY path
            """, ("@run", runId));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            hashes.Add(reader.GetString(0), reader.GetString(1));
        return hashes;
    }

    public static AtlasSourceEvent? Append(
        SqliteConnection db, SqliteTransaction tx, string runId,
        string workItemId, Dictionary<string, string> before)
    {
        var after = CaptureHashes(db, tx, runId);
        var changes = new List<AtlasSourceChange>();
        foreach (var entry in after.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!before.TryGetValue(entry.Key, out var hash))
                changes.Add(new(entry.Key, "Added", entry.Value));
            else if (hash != entry.Value)
                changes.Add(new(entry.Key, "Modified", entry.Value));
        }
        foreach (var entry in before.OrderBy(f => f.Key, StringComparer.Ordinal))
            if (!after.ContainsKey(entry.Key))
                changes.Add(new(entry.Key, "Removed", entry.Value));
        if (changes.Count == 0) return null;

        // Exact same revision recipe as CodeAtlasIndexer.Index:
        // SHA256(path + "|" + SHA256(content)), sorted by ordinal path.
        var revision = Sha(string.Join("\n", after
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => f.Key + "|" + f.Value)));
        var previous = TaskStore.Scalar(db, tx, """
            SELECT COALESCE(MAX(sequence),0) FROM engineering_atlas_events
            WHERE run_id=@run
            """, ("@run", runId));
        var sequence = int.Parse(previous!) + 1;
        var now = DateTimeOffset.UtcNow.ToString("O");
        TaskStore.Execute(db, tx, """
            INSERT INTO engineering_atlas_events(
                run_id,sequence,work_item_id,source_revision,
                changes_json,created_utc)
            VALUES(@run,@sequence,@item,@revision,@changes,@now)
            """, ("@run", runId), ("@sequence", sequence),
            ("@item", workItemId), ("@revision", revision),
            ("@changes", JsonSerializer.Serialize(changes)), ("@now", now));
        return new(runId, sequence, workItemId, revision,
            changes.AsReadOnly(), now);
    }

    public AtlasSourceEventPage Read(string taskId, string runId, int after)
    {
        if (after < 0) throw new StoreFailure("INVALID_INPUT");
        _ = engineering.Get(taskId, runId); // Enforce the existing project/run binding.
        using var db = tasks.Open();
        var last = int.Parse(TaskStore.Scalar(db, null, """
            SELECT COALESCE(MAX(sequence),0) FROM engineering_atlas_events
            WHERE run_id=@run
            """, ("@run", runId))!);
        var events = new List<AtlasSourceEvent>();
        using var cmd = TaskStore.Command(db, null, """
            SELECT sequence,work_item_id,source_revision,changes_json,created_utc
            FROM engineering_atlas_events WHERE run_id=@run AND sequence>@after
            ORDER BY sequence LIMIT @limit
            """, ("@run", runId), ("@after", after), ("@limit", PageSize));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var changes = JsonSerializer.Deserialize<AtlasSourceChange[]>(
                reader.GetString(3)) ?? [];
            events.Add(new(runId, reader.GetInt32(0), reader.GetString(1),
                reader.GetString(2), changes, reader.GetString(4)));
        }
        return new(runId, last, events.AsReadOnly());
    }
}
