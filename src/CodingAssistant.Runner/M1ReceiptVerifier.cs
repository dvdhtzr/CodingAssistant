using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Runner;

/// <summary>
/// Bounded, independent HOST-side inspection of the legacy M1 run ledger
/// and its evaluator evidence. NEVER trust exit code or stdout as PASS.
/// </summary>
public sealed record M1Receipt(
    string State, string? M1RunId, bool Verified);

public static class M1ReceiptVerifier
{
    private static readonly Regex RunIdPattern = new(
        "^[0-9a-f]{32}\\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FilePattern = new(
        "^m112-approved-coding-[0-9a-f]{12}\\.json\\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static M1Receipt Inspect(string repositoryRoot, string m2RunId)
    {
        if (!RunIdPattern.IsMatch(m2RunId))
            throw new InvalidOperationException("Invalid exact run identity");
        var dbPath = Path.Combine(repositoryRoot,
            "artifacts", "m1", "private-ledger", "runs.sqlite3");
        if (!File.Exists(dbPath) ||
            (File.GetAttributes(dbPath) & FileAttributes.ReparsePoint) != 0)
            return new M1Receipt("Missing", null, false);

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT run_id,state,plan_hash,result_code,evidence_name
            FROM runs WHERE request_id=@request
            """;
        cmd.Parameters.AddWithValue("@request", m2RunId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return new M1Receipt("Missing", null, false);
        var id = reader.GetString(0);
        var state = reader.GetString(1);
        var plan = reader.IsDBNull(2) ? null : reader.GetString(2);
        var code = reader.IsDBNull(3) ? null : reader.GetString(3);
        var evidence = reader.IsDBNull(4) ? null : reader.GetString(4);
        if (!Regex.IsMatch(id, "^[0-9a-f]{24}\\z",
                RegexOptions.CultureInvariant))
            return new M1Receipt("Untrusted", null, false);

        if (state != "SUCCEEDED")
            return new M1Receipt(state, id, false);
        if (code != "GATEWAY_APPROVED_CODING_PASS" ||
            plan is null || evidence is null ||
            !FilePattern.IsMatch(evidence))
            return new M1Receipt("Untrusted", id, false);

        var filePath = Path.Combine(repositoryRoot, "artifacts", "m1", evidence);
        if (!File.Exists(filePath) ||
            (File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(filePath).Length is < 32 or > 128 * 1024)
            return new M1Receipt("Untrusted", id, false);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(filePath));
            var root = document.RootElement;
            var gateway = root.GetProperty("gateway");
            var broker = root.GetProperty("broker");
            var evaluator = root.GetProperty("evaluator");
            bool yes(string name) => root.GetProperty(name).GetBoolean();

            var verified =
                root.GetProperty("status").GetString() ==
                    "GATEWAY_APPROVED_CODING_PASS" &&
                root.GetProperty("plan_hash").GetString() == plan &&
                root.GetProperty("model").GetString() == "qwen3.5:4b" &&
                root.GetProperty("model_digest").GetString() is { Length: > 30 } &&
                yes("approved") &&
                yes("fixture_unchanged") &&
                yes("sentinel_unchanged") &&
                !yes("original_repo_mounted") &&
                !yes("external_network_enabled") &&
                !yes("git_remote_enabled") &&
                gateway.GetProperty("removed").GetBoolean() &&
                gateway.GetProperty("rpc_nonzero").GetBoolean() == false &&
                gateway.GetProperty("rpc").GetProperty("turn_completed").GetBoolean() &&
                broker.GetProperty("verified").GetInt32() >= 1 &&
                broker.GetProperty("denied").GetInt32() == 0 &&
                broker.GetProperty("failed").GetInt32() == 0 &&
                evaluator.GetProperty("verified").GetBoolean() &&
                evaluator.GetProperty("status").GetString() == "VERIFIED";
            return new M1Receipt(verified ? "SUCCEEDED" : "Untrusted",
                id, verified);
        }
        catch (Exception error) when (error is JsonException or
            KeyNotFoundException or InvalidOperationException or
            FormatException or IOException)
        {
            // Missing, malformed or altered evidence can never become PASS.
            return new M1Receipt("Untrusted", id, false);
        }
    }
}
