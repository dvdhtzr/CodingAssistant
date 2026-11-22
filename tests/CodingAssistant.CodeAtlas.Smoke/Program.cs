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
Check(graph.Coverage == "STATIC_SEMANTIC",
    "trusted Roslyn resolves local C# relationships without executing source");
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

// M4: only Roslyn-resolved, source-backed relationships may become graph edges.
var semanticFiles = new[]
{
    File("Sample/Sample.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>"),
    File("Sample/Domain/IStore.cs", """
        namespace Sample;
        public interface IStore { void Save(); }
        """),
    File("Sample/Domain/SqlStore.cs", """
        namespace Sample;
        public class SqlStore : IStore { public void Save() {} }
        """),
    File("Sample/Services/OrderService.cs", """
        namespace Sample;
        public abstract class BaseService {}
        public class OrderService : BaseService
        {
            private readonly IStore _store;
            public OrderService(IStore store) { _store = store; }
            public void Place() { _store.Save(); Helper(); }
            private void Helper() {}
        }
        """)
};
var semantic = CodeAtlasIndexer.Index("run-semantic", semanticFiles);
Check(semantic.Coverage == "STATIC_SEMANTIC",
    "complete C# source gets static semantic coverage");
string SemanticId(string kind, string ending) =>
    semantic.Nodes.Single(n => n.Kind == kind &&
        n.QualifiedName.EndsWith(ending, StringComparison.Ordinal)).Id;
bool Link(string from, string to, string kind) =>
    semantic.Edges.Any(e => e.SourceId == from &&
        e.TargetId == to && e.Kind == kind &&
        e.Resolution == "ResolvedStatic" &&
        e.EvidencePath?.EndsWith(".cs", StringComparison.Ordinal) == true &&
        e.EvidenceLine > 0);
var orderService = SemanticId("Class", "|Sample.OrderService");
var baseService = SemanticId("Class", "|Sample.BaseService");
var storeContract = SemanticId("Interface", "|Sample.IStore");
var concreteStore = SemanticId("Class", "|Sample.SqlStore");
var place = SemanticId("Method", "|Sample.OrderService.Place()");
var helper = SemanticId("Method", "|Sample.OrderService.Helper()");
var saveContract = SemanticId("Method", "|Sample.IStore.Save()");
Check(Link(orderService, baseService, "Inherits"),
    "type inheritance is resolved with source evidence");
Check(Link(concreteStore, storeContract, "Implements"),
    "interface implementation is statically resolved");
Check(Link(orderService, storeContract, "ConstructorDependsOn"),
    "constructor dependency points to the declared interface");
Check(Link(place, helper, "Calls"),
    "same-class invocation resolves its exact declared target");
Check(Link(place, saveContract, "Calls"),
    "interface dispatch records compile-time target, not imagined implementation");
Check(!semantic.Edges.Any(e => e.Kind == "Calls" &&
    e.TargetId == SemanticId("Method", "|Sample.SqlStore.Save()") &&
    e.SourceId == place),
    "interface call is never misreported as the concrete runtime dispatch");

var isolated = CodeAtlasIndexer.Index("run-scope", new[]
{
    File("One/One.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>"),
    File("One/A.cs", """
        namespace One;
        public class A { public void Go() { Two.B.Action(); } }
        """),
    File("Two/Two.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>"),
    File("Two/B.cs", """
        namespace Two;
        public class B { public static void Action() {} }
        """),
});
Check(!isolated.Edges.Any(e => e.Kind == "Calls"),
    "no cross-project calls without declared project reference semantics");

var projectGraph = CodeAtlasIndexer.Index("run-project", new[]
{
    File("One/One.csproj", """
        <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
        <ProjectReference Include="../Two/Two.csproj"/>
        </ItemGroup></Project>
        """),
    File("Two/Two.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />")
});
var sourceProject = projectGraph.Nodes.Single(n =>
    n.Kind == "Project" && n.Path == "One/One.csproj");
var targetProject = projectGraph.Nodes.Single(n =>
    n.Kind == "Project" && n.Path == "Two/Two.csproj");
Check(projectGraph.Edges.Any(e => e.Kind == "ProjectReferences" &&
    e.SourceId == sourceProject.Id && e.TargetId == targetProject.Id &&
    e.Resolution == "LiteralProjectReference" &&
    e.EvidencePath == "One/One.csproj" && e.EvidenceLine > 0),
    "literal in-repo project reference is grounded without evaluating MSBuild");
var conditional = CodeAtlasIndexer.Index("run-project", new[]
{
    File("One/One.csproj", """
        <Project><ItemGroup Condition="'$(Enable)' == 'true'">
        <ProjectReference Include="../Two/Two.csproj"/>
        </ItemGroup></Project>
        """),
    File("Two/Two.csproj", "<Project/>")
});
Check(!conditional.Edges.Any(e => e.Kind == "ProjectReferences") &&
    conditional.Diagnostics.Any(d => d.StartsWith(
        "CONDITIONAL_PROJECT_REFERENCE_UNRESOLVED:", StringComparison.Ordinal)),
    "computed or conditional project dependency is not claimed as active");
var externalXml = CodeAtlasIndexer.Index("run-project", new[]
{
    File("One/One.csproj", """
        <!DOCTYPE Project [ <!ENTITY external SYSTEM "file:///etc/passwd"> ]>
        <Project>&external;</Project>
        """)
});
Check(externalXml.Diagnostics.Any(d => d.StartsWith(
    "INVALID_PROJECT_XML:", StringComparison.Ordinal)),
    "untrusted MSBuild XML DTD is rejected without reading external resources");

// M4 path tracing: honor edge direction, bounded hops and actual evidence.
var route = CodeAtlasPathFinder.Trace(semantic, place, helper);
Check(route.Found && route.NodeIds.SequenceEqual(new[] { place, helper }) &&
    route.Edges.Single().Kind == "Calls",
    "method call trace identifies the real direct edge");
Check(!CodeAtlasPathFinder.Trace(semantic, helper, place).Found,
    "reverse call direction does not fabricate a caller relationship");
Check(CodeAtlasPathFinder.Trace(semantic, place, saveContract,
    maxHops: 1).Found,
    "single-hop method call tracing is available");
var indirect = CodeAtlasPathFinder.Trace(semantic, orderService, helper,
    maxHops: 3);
Check(indirect.Found && indirect.NodeIds.First() == orderService &&
    indirect.NodeIds.Last() == helper,
    "type containment followed by a method call has bounded traversal");
Check(!CodeAtlasPathFinder.Trace(semantic, orderService, helper,
    maxHops: 1).Found,
    "hop limit rejects longer relationships");
var pathRejected = false;
try { _ = CodeAtlasPathFinder.Trace(semantic, place, helper, maxHops: 13); }
catch (StoreFailure e) when (e.Code == "INVALID_INPUT") { pathRejected = true; }
Check(pathRejected, "unbounded graph path request is rejected");
pathRejected = false;
try { _ = CodeAtlasPathFinder.Trace(semantic, "unknown", helper); }
catch (StoreFailure e) when (e.Code == "NOT_FOUND") { pathRejected = true; }
Check(pathRejected, "unknown symbol identity is rejected");

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


// The cursor API must enforce run/project scope even for a guessed valid ID.
var privateDirectory = Path.Combine(Path.GetTempPath(),
    "ca-atlas-scope-" + Guid.NewGuid().ToString("N"));
try
{
    var tasks = new TaskStore(privateDirectory);
    var interviews = new InterviewStore(tasks);
    var plans = new ImplementationPlanStore(tasks, interviews);
    var engineering = new EngineeringRunStore(tasks, plans);
    engineering.Initialize();
    var ledger = new EngineeringAtlasLedger(tasks, engineering);
    ledger.Initialize();
    var firstTask = new string('a', 32);
    var secondTask = new string('c', 32);
    var firstRun = new string('b', 32);
    var secondRun = new string('d', 32);
    using (var db = new SqliteConnection(
        "Data Source=" + Path.Combine(privateDirectory, "codingassistant.db") +
        ";Pooling=False"))
    {
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO tasks(id,request_id,title,objective,status,created_utc)
            VALUES('{firstTask}','fixture-one','First','Atlas scope','Draft','2026-11-22T00:00:00Z');
            INSERT INTO tasks(id,request_id,title,objective,status,created_utc)
            VALUES('{secondTask}','fixture-two','Second','Atlas scope','Draft','2026-11-22T00:00:00Z');
            INSERT INTO engineering_runs(
                id,task_id,plan_revision,plan_sha256,spec_version,spec_sha256,
                architecture_sha256,model,state,phase,created_utc,updated_utc)
            VALUES('{firstRun}','{firstTask}',1,'{new string('a',64)}',1,
                '{new string('a',64)}','{new string('a',64)}','local',
                'Generated','SourceOnlyGenerated','2026-11-22T00:00:00Z',
                '2026-11-22T00:00:00Z');
            INSERT INTO engineering_runs(
                id,task_id,plan_revision,plan_sha256,spec_version,spec_sha256,
                architecture_sha256,model,state,phase,created_utc,updated_utc)
            VALUES('{secondRun}','{secondTask}',1,'{new string('a',64)}',1,
                '{new string('a',64)}','{new string('a',64)}','local',
                'Generated','SourceOnlyGenerated','2026-11-22T00:00:00Z',
                '2026-11-22T00:00:00Z');
            INSERT INTO engineering_atlas_events(
                run_id,sequence,work_item_id,source_revision,changes_json,
                created_utc)
            VALUES('{secondRun}',1,'W1','{new string('b',64)}','[]',
                '2026-11-22T00:00:00Z');
            """;
        cmd.ExecuteNonQuery();
    }
    var page = ledger.Read(secondTask, secondRun, 0);
    Check(page.LastSequence == 1 && page.Events.Single().Sequence == 1,
        "authorized project can replay a committed source cursor");
    Check(ledger.Read(secondTask, secondRun, 1).Events.Count == 0,
        "cursor page is idempotent and returns no duplicate events");
    bool denied = false;
    try { _ = ledger.Read(firstTask, secondRun, 0); }
    catch (StoreFailure error) when (error.Code == "NOT_FOUND") { denied = true; }
    Check(denied, "another project's run is not readable by guessed ID");
    denied = false;
    try { _ = ledger.Read(secondTask, secondRun, -1); }
    catch (StoreFailure error) when (error.Code == "INVALID_INPUT") { denied = true; }
    Check(denied, "negative replay cursors are rejected");
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(privateDirectory))
        Directory.Delete(privateDirectory, recursive: true);
}

Console.WriteLine("CODE_ATLAS_SOURCE_GRAPH_PASS: " + checks + " assertions");
