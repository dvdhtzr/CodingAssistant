using CodingAssistant.Api;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

var checks = 0;
void Check(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException("Code Atlas: " + message);
    checks++;
}
EngineeringSourceFile File(string path, string content) =>
    new(path, "Test", content, "fixture-hash", "fixture-task",
        "2026-11-22T00:00:00Z");

var input = new[]
{
    File("Demo/Demo.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"),
    File("Demo/Domain/ItemA.cs", """
        namespace Demo.Domain;
        public partial class Item
        {
            public int Id { get; set; }
            public void Save() {}
        }
        """),
    File("Demo/Domain/ItemB.cs", """
        namespace Demo.Domain;
        public partial class Item
        {
            public void Load(string value) {}
        }
        """),
    File("Demo/Domain/IItemStore.cs", """
        namespace Demo.Domain;
        public interface IItemStore { Item Find(int id); }
        """),
    File("Demo/Services/ItemService.cs", """
        namespace Demo.Services;
        public class ItemService
        {
            public ItemService(Demo.Domain.IItemStore store) {}
            public void Add(Demo.Domain.Item item) {}
            public void Add(int id) {}
        }
        """),
    File("Demo/Web/app.ts", "export const add = (n: number) => n + 1;")
};
var graph = CodeAtlasIndexer.Index("run-one", input);
Check(graph.Coverage == "SYNTAX_ONLY", "syntax-only coverage");
Check(graph.Diagnostics.Count == 0, "clean diagnostics");
Check(graph.Nodes.Count(n => n.Kind == "Project") == 1, "one project");
Check(graph.Nodes.Count(n => n.Kind == "File") == 6, "all file types shown");
Check(graph.Nodes.Any(n => n.Kind == "File" && n.Path == "Demo/Web/app.ts"),
    "TypeScript file included without pretending to analyze its methods");
Check(graph.Nodes.Count(n => n.Kind == "Class" &&
    n.QualifiedName.EndsWith("Demo.Domain.Item", StringComparison.Ordinal)) == 1,
    "partial C# type declarations are one logical class");
Check(graph.Nodes.Count(n => n.Kind == "Method" &&
    n.Name.StartsWith("Add(", StringComparison.Ordinal)) == 2,
    "method overloads remain distinct");
Check(graph.Nodes.Count(n => n.Kind == "Constructor") == 1,
    "constructor indexed");
Check(graph.Nodes.Any(n => n.Kind == "Interface" &&
    n.Name == "IItemStore"), "interface indexed");
Check(graph.Edges.Any(e => e.Kind == "Declares"),
    "source declaration edges exist");
Check(graph.Edges.All(e =>
    graph.Nodes.Any(n => n.Id == e.SourceId) &&
    graph.Nodes.Any(n => n.Id == e.TargetId)),
    "all graph edges have valid endpoints");
var reordered = CodeAtlasIndexer.Index("run-one",
    input.Reverse().ToArray());
Check(graph.SourceRevision == reordered.SourceRevision,
    "content revision independent of input order");
Check(graph.Nodes.Select(n => n.Id).SequenceEqual(
    reordered.Nodes.Select(n => n.Id)), "node identities deterministic");
var modified = CodeAtlasIndexer.Index("run-one",
    input.Select(f => f.Path == "Demo/Web/app.ts"
        ? f with { Content = "export const changed = true;" }
        : f).ToArray());
Check(graph.SourceRevision != modified.SourceRevision,
    "revisions reflect changed source");
var bad = CodeAtlasIndexer.Index("run-one",
    input.Concat(new[]
    {
        File("../private.cs", "class Escape {}"),
        File("Demo/Broken.cs", "namespace Demo; class Broken { public void")
    }).ToArray());
Check(bad.Diagnostics.Contains("UNSAFE_PATH_EXCLUDED"),
    "unsafe source path rejected");
Check(bad.Diagnostics.Any(d => d.StartsWith("INCOMPLETE_CSHARP:",
    StringComparison.Ordinal)), "incomplete C# source diagnosed");
Check(bad.Coverage == "PARTIAL_SYNTAX",
    "invalid syntax never reported fully analyzed");
Check(!bad.Nodes.Any(n => n.Path == "../private.cs"),
    "unsafe source not represented");

// M3: exercise a real SQLite commit/rollback and immutable source-change
// cursor without launching any untrusted project code or model.
var database = Path.Combine(Path.GetTempPath(),
    "ca-atlas-ledger-" + Guid.NewGuid().ToString("N") + ".db");
try
{
    void Sql(SqliteConnection db, SqliteTransaction? tx, string statement)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = statement;
        command.ExecuteNonQuery();
    }
    string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    var run = "b".PadLeft(32, 'b');
    var original = "namespace Demo; public class Example { }";
    var updated = "namespace Demo; public class Example { public void Go() {} }";
    using (var db = new SqliteConnection("Data Source=" + database + ";Pooling=False"))
    {
        db.Open();
        Sql(db, null, """
            CREATE TABLE engineering_runs (id TEXT PRIMARY KEY);
            CREATE TABLE engineering_run_files (
                run_id TEXT NOT NULL, path TEXT NOT NULL,
                sha256 TEXT NOT NULL, PRIMARY KEY(run_id,path));
            CREATE TABLE engineering_atlas_events (
                run_id TEXT NOT NULL REFERENCES engineering_runs(id),
                sequence INTEGER NOT NULL, work_item_id TEXT NOT NULL,
                source_revision TEXT NOT NULL, changes_json TEXT NOT NULL,
                created_utc TEXT NOT NULL, PRIMARY KEY(run_id,sequence));
            CREATE TABLE engineering_atlas_graph_deltas (
                run_id TEXT NOT NULL, sequence INTEGER NOT NULL,
                delta_json TEXT NOT NULL, PRIMARY KEY(run_id,sequence),
                FOREIGN KEY(run_id,sequence)
                  REFERENCES engineering_atlas_events(run_id,sequence));
            INSERT INTO engineering_runs(id)
            VALUES('bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb');
            """);
        using (var tx = db.BeginTransaction())
        {
            var before = EngineeringAtlasLedger.CaptureHashes(db, tx, run);
            Check(before.Count == 0, "first revision has no previous sources");
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO engineering_run_files(run_id,path,sha256)
                    VALUES($run,$path,$hash)
                    """;
                cmd.Parameters.AddWithValue("$run", run);
                cmd.Parameters.AddWithValue("$path", "Example.cs");
                cmd.Parameters.AddWithValue("$hash", Hash(original));
                cmd.ExecuteNonQuery();
            }
            var beforeGraph = CodeAtlasIndexer.Index(run,
                Array.Empty<EngineeringSourceFile>());
            var afterGraph = CodeAtlasIndexer.Index(run,
                new[] { File("Example.cs", original) });
            var evt = EngineeringAtlasLedger.Append(db, tx, run, "W1", before,
                beforeGraph, afterGraph);
            Check(evt?.Sequence == 1 && evt.Changes.Count == 1 &&
                evt.Changes[0].Kind == "Added",
                "new persisted source creates source-change event");
            Check(evt!.SourceRevision == afterGraph.SourceRevision,
                "event revision agrees exactly with source graph");
            Check(evt.GraphDelta?.AddedNodeIds.Contains(afterGraph.Nodes.Single(n =>
                n.Kind == "Class").Id) == true,
                "new source records an actual added class node");
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            var before = EngineeringAtlasLedger.CaptureHashes(db, tx, run);
            Check(EngineeringAtlasLedger.Append(db, tx, run, "W2", before) is null,
                "same source hash produces no phantom change");
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            var before = EngineeringAtlasLedger.CaptureHashes(db, tx, run);
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE engineering_run_files SET sha256=$hash
                    WHERE run_id=$run AND path='Example.cs'
                    """;
                cmd.Parameters.AddWithValue("$hash", Hash(updated));
                cmd.Parameters.AddWithValue("$run", run);
                cmd.ExecuteNonQuery();
            }
            var beforeGraph = CodeAtlasIndexer.Index(run,
                new[] { File("Example.cs", original) });
            var afterGraph = CodeAtlasIndexer.Index(run,
                new[] { File("Example.cs", updated) });
            var evt = EngineeringAtlasLedger.Append(db, tx, run, "W3", before,
                beforeGraph, afterGraph);
            Check(evt?.Sequence == 2 && evt.Changes.Single().Kind == "Modified",
                "changed source is versioned once and marked Modified");
            Check(evt!.SourceRevision == afterGraph.SourceRevision,
                "replacement revision matches independently indexed source");
            Check(evt.GraphDelta?.AddedNodeIds.Contains(afterGraph.Nodes.Single(n =>
                n.Kind == "Method").Id) == true,
                "new method is recorded in the persisted graph delta");
            Check(evt.GraphDelta?.UpdatedNodeIds.Contains(afterGraph.Nodes.Single(n =>
                n.Kind == "Class").Id) == true,
                "modified source records its retained class as updated");
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            var before = EngineeringAtlasLedger.CaptureHashes(db, tx, run);
            Sql(db, tx, """
                DELETE FROM engineering_run_files
                WHERE run_id='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
                """);
            var evt = EngineeringAtlasLedger.Append(db, tx, run, "W4", before);
            Check(evt?.Changes.Single().Kind == "Removed",
                "deleted source can be represented as a real removal");
            tx.Rollback();
        }
    }
    using (var db = new SqliteConnection("Data Source=" + database + ";Pooling=False"))
    {
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM engineering_atlas_events
            WHERE run_id='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
            """;
        Check(Convert.ToInt32(cmd.ExecuteScalar()) == 2,
            "SQLite restart retains two committed revisions, not rollback");
        cmd.CommandText = "SELECT COUNT(*) FROM engineering_atlas_graph_deltas";
        Check(Convert.ToInt32(cmd.ExecuteScalar()) == 2,
            "graph deltas survive restart and rolled-back updates do not");
        cmd.CommandText = """
            SELECT changes_json FROM engineering_atlas_events
            WHERE run_id='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
            ORDER BY sequence DESC LIMIT 1
            """;
        var payload = (string?)cmd.ExecuteScalar() ?? "";
        Check(payload.Contains("Modified", StringComparison.Ordinal) &&
            !payload.Contains("namespace Demo", StringComparison.Ordinal),
            "recorded source changes contain hashes, not source contents");
    }
}
finally
{
    SqliteConnection.ClearAllPools();
    if (System.IO.File.Exists(database)) System.IO.File.Delete(database);
}

Console.WriteLine("CODE_ATLAS_SOURCE_GRAPH_PASS: " + checks + " assertions");
