using System.Text.Json;
using CodingAssistant.Runner;
using Microsoft.Data.Sqlite;

/// <summary>
/// Model-free host evidence acceptance. All fixtures below are synthetic:
/// tests validate the parser and tamper rejection, NOT model qualification.
/// </summary>
public static class M1ReceiptChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
                throw new Exception("M1 receipt smoke assertion failed: " + name);
            count++;
        }

        using var temp = new TemporaryRepository();
        const string request = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string m1Run = "aaaaaaaaaaaaaaaaaaaaaaaa";
        const string plan = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        const string evidence = "m112-approved-coding-001122aabbcc.json";
        var db = temp.Ledger;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = db, Mode = SqliteOpenMode.ReadWriteCreate,
                   Pooling = false
               }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE runs (
                  run_id TEXT,request_id TEXT,state TEXT,
                  plan_hash TEXT,result_code TEXT,evidence_name TEXT
                );
                INSERT INTO runs (run_id,request_id,state,plan_hash,result_code,evidence_name)
                VALUES (@run,@request,'SUCCEEDED',@plan,
                'GATEWAY_APPROVED_CODING_PASS',@evidence);
                """;
            command.Parameters.AddWithValue("@run", m1Run);
            command.Parameters.AddWithValue("@request", request);
            command.Parameters.AddWithValue("@plan", plan);
            command.Parameters.AddWithValue("@evidence", evidence);
            command.ExecuteNonQuery();
        }

        var proofFile = Path.Combine(temp.Artifacts, evidence);
        var valid = new
        {
            status = "GATEWAY_APPROVED_CODING_PASS",
            plan_hash = plan,
            model = "qwen3.5:4b",
            model_digest = "sha256:" + new string('e', 64),
            approved = true,
            fixture_unchanged = true,
            sentinel_unchanged = true,
            original_repo_mounted = false,
            external_network_enabled = false,
            git_remote_enabled = false,
            gateway = new
            {
                removed = true,
                rpc_nonzero = false,
                rpc = new { turn_completed = true }
            },
            broker = new { verified = 6, denied = 0, failed = 0 },
            evaluator = new { verified = true, status = "VERIFIED" }
        };

        File.WriteAllText(proofFile, JsonSerializer.Serialize(valid));
        var receipt = M1ReceiptVerifier.Inspect(temp.Root, request);
        Check(receipt.Verified && receipt.State == "SUCCEEDED" &&
              receipt.M1RunId == m1Run,
              "synthetic matching ledger/evaluator payload recognized");

        var tampered = JsonSerializer.Serialize(valid).Replace(
            "\"verified\":true,\"status\":\"VERIFIED\"",
            "\"verified\":false,\"status\":\"BLOCKED\"",
            StringComparison.Ordinal);
        File.WriteAllText(proofFile, tampered);
        Check(!M1ReceiptVerifier.Inspect(temp.Root, request).Verified,
              "independent evaluator flag tampering never verifies");

        File.WriteAllText(proofFile, JsonSerializer.Serialize(valid).Replace(
            plan, new string('f', 64), StringComparison.Ordinal));
        Check(!M1ReceiptVerifier.Inspect(temp.Root, request).Verified,
              "changed execution plan hash cannot verify");

        File.WriteAllText(proofFile, "not-json");
        Check(!M1ReceiptVerifier.Inspect(temp.Root, request).Verified,
              "truncated evidence cannot verify");

        File.Delete(proofFile);
        Check(!M1ReceiptVerifier.Inspect(temp.Root, request).Verified,
              "absent evidence cannot verify");

        Check(M1ReceiptVerifier.Inspect(temp.Root,
              "cccccccccccccccccccccccccccccccc").State == "Missing",
              "missing host ledger row does not imply pass");
        return count;
    }

    private sealed class TemporaryRepository : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "ca-m2-receipt-" + Guid.NewGuid().ToString("N"));
        public string Artifacts => Path.Combine(Root, "artifacts", "m1");
        public string Ledger => Path.Combine(
            Artifacts, "private-ledger", "runs.sqlite3");

        public TemporaryRepository()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Ledger)!);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
