using CodingAssistant.Api;

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
Console.WriteLine("CODE_ATLAS_SOURCE_GRAPH_PASS: " + checks + " assertions");
