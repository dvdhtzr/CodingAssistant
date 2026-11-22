using CodingAssistant.Api;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
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
Check(graph.Coverage == "MIXED_CSHARP_STATIC_ECMA_STRUCTURE",
    "C# static semantics and JS/TS structure are reported distinctly");
Check(graph.Diagnostics.Count == 0, "clean diagnostics");
Check(graph.Nodes.Count(n => n.Kind == "Project") == 1, "one project");
Check(graph.Nodes.Count(n => n.Kind == "File") == 6, "all file types shown");
Check(graph.Nodes.Any(n => n.Kind == "File" && n.Path == "Demo/Web/app.ts"),
    "TypeScript file remains source-backed");
Check(graph.Nodes.Any(n => n.Kind == "Function" &&
    n.Path == "Demo/Web/app.ts" && n.Name == "add(arrow)"),
    "saved TypeScript arrow function is visible without claiming call resolution");
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
Check(bad.Coverage == "MIXED_PARTIAL_CSHARP_ECMA_STRUCTURE",
    "invalid C# plus TypeScript must never be reported fully analyzed");
Check(!bad.Nodes.Any(n => n.Path == "../private.cs"),
    "unsafe source not represented");

// M8.6: read-only Python declaration indexing without Python execution.
var pythonFiles = new[]
{
    File("pkg/models.py", """"
        # class Fake: pass
        quoted = "def Phantom(): pass"
        script = """
        class FalsePositive:
            def never(): pass
        """
        class Worker:
            def __init__(self, value):
                self.value = value
            async def run(self, text):
                def local():
                    return "class Strange:"
                return local()
        def package_func(item):
            return item
        """"),
    File("pkg/more.py", """
        class Worker:
            def run(self): pass
        """)
};
var pythonGraph = CodeAtlasIndexer.Index("python-run", pythonFiles);
Check(pythonGraph.Coverage == "PYTHON_STRUCTURE_ONLY" &&
    pythonGraph.Diagnostics.Count == 0,
    "valid Python produces explicit source-structure-only coverage");
Check(pythonGraph.Nodes.Count(n => n.Kind == "Class" &&
    n.Name == "Worker") == 2,
    "same-named Python classes in separate files remain distinct");
var pythonClass = pythonGraph.Nodes.Single(n =>
    n.Kind == "Class" && n.Name == "Worker" &&
    n.Path == "pkg/models.py");
Check(pythonClass.Line == 7,
    "Python declaration includes exact persisted source line");
var pythonConstructor = pythonGraph.Nodes.Single(n =>
    n.Kind == "Constructor" && n.Name == "__init__" &&
    n.Path == "pkg/models.py");
Check(pythonConstructor.ParentId == pythonClass.Id &&
    pythonConstructor.Line == 8,
    "Python __init__ is a class-owned source constructor");
var pythonMethod = pythonGraph.Nodes.Single(n =>
    n.Kind == "Method" && n.Name == "run" &&
    n.Path == "pkg/models.py");
Check(pythonMethod.ParentId == pythonClass.Id &&
    pythonMethod.Line == 10,
    "async Python methods are linked to the containing class");
var pythonLocal = pythonGraph.Nodes.Single(n =>
    n.Kind == "Function" && n.Name == "local");
Check(pythonLocal.ParentId == pythonMethod.Id &&
    pythonLocal.Line == 11,
    "nested Python function scope is captured without invented calls");
Check(pythonGraph.Nodes.Any(n =>
    n.Kind == "Function" && n.Name == "package_func" &&
    n.Path == "pkg/models.py") &&
    !pythonGraph.Nodes.Any(n =>
        n.Name is "Fake" or "Phantom" or "FalsePositive" or
        "never" or "Strange"),
    "Python comment, string and triple-quote decoys never become nodes");
Check(!pythonGraph.Edges.Any(e => e.Kind is "Calls" or "Inherits" or
    "Implements" or "ConstructorDependsOn"),
    "Python syntax analysis never claims resolved runtime relationships");
Check(pythonGraph.Edges.All(e =>
    pythonGraph.Nodes.Any(n => n.Id == e.SourceId) &&
    pythonGraph.Nodes.Any(n => n.Id == e.TargetId)),
    "Python structure never creates dangling graph relationships");
var pythonReordered = CodeAtlasIndexer.Index("python-run",
    pythonFiles.Reverse().ToArray());
Check(pythonGraph.SourceRevision == pythonReordered.SourceRevision &&
    pythonGraph.Nodes.Select(n => n.Id).SequenceEqual(
        pythonReordered.Nodes.Select(n => n.Id)),
    "Python graph identities are deterministic across input order");
var mixedPython = CodeAtlasIndexer.Index("mixed-python-run",
    pythonFiles.Append(File("ui/app.ts", "export const app = () => 1;"))
        .ToArray());
Check(mixedPython.Coverage ==
    "ECMA_STRUCTURE_ONLY+PYTHON_STRUCTURE_ONLY" &&
    mixedPython.Nodes.Any(n => n.Kind == "Function" &&
        n.Path == "ui/app.ts") &&
    mixedPython.Nodes.Any(n => n.Kind == "Class" &&
        n.Path == "pkg/models.py"),
    "mixed JS/TS plus Python retains distinct coverage and both structures");
var invalidPython = CodeAtlasIndexer.Index("python-run", new[]
{
    File("pkg/invalid.py", """"
        class Complete:
            pass
        def unsupported(
          arg):
            pass
        message = """
        class Ghost:
            pass
        """"),
    File("../escape.py", "class Escaped: pass")
});
Check(invalidPython.Coverage == "PARTIAL_PYTHON_STRUCTURE" &&
    invalidPython.Diagnostics.Any(d =>
        d.StartsWith("PYTHON_UNSUPPORTED_DEFINITION:",
            StringComparison.Ordinal)) &&
    invalidPython.Diagnostics.Any(d =>
        d.StartsWith("PYTHON_UNCLOSED_TRIPLE_QUOTE:",
            StringComparison.Ordinal)),
    "incomplete Python source reports partial coverage, never success");
Check(invalidPython.Diagnostics.Contains("UNSAFE_PATH_EXCLUDED") &&
    !invalidPython.Nodes.Any(n => n.Name == "Escaped" ||
        n.Name == "Ghost"),
    "Python unsafe paths and unterminated strings create no graph nodes");

// M8.9: explicit Python imports may link ONLY to saved local module files.
var pythonModuleSources = new[]
{
    File("src/pkg/api/main.py", """"
        import src.pkg.api.service as service
        from .helpers import assist
        from ..common.formatter import Format
        import json
        from .missing import Missing
        from .....escape import Nope
        from .duplicate import Value
        # from .decoy import Hidden
        literal = "import src.pkg.api.decoy"
        sample = """
        from .decoy import Hidden
        """
        def dynamic():
            import src.pkg.api.decoy
        """"),
    File("src/pkg/api/service.py", "class Service: pass"),
    File("src/pkg/api/helpers.py", "def assist(): pass"),
    File("src/pkg/common/formatter.py", "class Format: pass"),
    File("src/pkg/api/duplicate.py", "class One: pass"),
    File("src/pkg/api/duplicate/__init__.py", "class Two: pass"),
    File("src/pkg/api/decoy.py", "class Hidden: pass")
};
var pythonModules = CodeAtlasIndexer.Index("python-import-run",
    pythonModuleSources);
var pythonFileIds = pythonModules.Nodes.Where(n => n.Kind == "File" &&
    n.Path is not null).ToDictionary(n => n.Path!, n => n.Id);
var pythonLinks = pythonModules.Edges.Where(e =>
    e.Kind == "ModuleImports").ToArray();
Check(pythonLinks.Length == 3 && pythonLinks.All(e =>
    e.SourceId == pythonFileIds["src/pkg/api/main.py"] &&
    e.EvidencePath == "src/pkg/api/main.py" &&
    e.EvidenceLine > 0 &&
    e.Resolution == "ExplicitPythonModuleSource"),
    "Python module links carry actual source path and precise line evidence");
Check(new[] { "src/pkg/api/service.py", "src/pkg/api/helpers.py",
    "src/pkg/common/formatter.py" }.All(path =>
    pythonLinks.Any(e => e.TargetId == pythonFileIds[path])),
    "absolute and relative Python imports reference exact saved modules");
Check(!pythonLinks.Any(e =>
    e.TargetId == pythonFileIds["src/pkg/api/decoy.py"] ||
    e.TargetId == pythonFileIds["src/pkg/api/duplicate.py"] ||
    e.TargetId == pythonFileIds["src/pkg/api/duplicate/__init__.py"]),
    "comments, quoted text, nested imports and ambiguous modules make no links");
Check(pythonModules.Diagnostics.Any(d =>
    d.StartsWith("PYTHON_LOCAL_IMPORT_UNRESOLVED:",
        StringComparison.Ordinal)) &&
    pythonModules.Diagnostics.Any(d =>
    d.StartsWith("PYTHON_LOCAL_IMPORT_OUTSIDE_ROOT:",
        StringComparison.Ordinal)) &&
    pythonModules.Diagnostics.Any(d =>
    d.StartsWith("PYTHON_LOCAL_IMPORT_AMBIGUOUS:",
        StringComparison.Ordinal)) &&
    pythonModules.Coverage == "PARTIAL_PYTHON_STRUCTURE",
    "missing, escaping and ambiguous relative imports are explicitly partial");
var pythonImportTrace = CodeAtlasPathFinder.Trace(pythonModules,
    pythonFileIds["src/pkg/api/main.py"],
    pythonFileIds["src/pkg/common/formatter.py"]);
Check(pythonImportTrace.Found &&
    pythonImportTrace.Edges.Count == 1 &&
    pythonImportTrace.Edges[0].Kind == "ModuleImports",
    "Python source module links are navigable in bounded path tracing");
var pythonSourcesReordered = CodeAtlasIndexer.Index("python-import-run",
    pythonModuleSources.Reverse().ToArray());
Check(pythonModules.SourceRevision == pythonSourcesReordered.SourceRevision &&
    pythonModules.Edges.Select(e => (e.Kind, e.SourceId, e.TargetId))
        .SequenceEqual(pythonSourcesReordered.Edges.Select(e =>
        (e.Kind, e.SourceId, e.TargetId))),
    "Python module relationships remain deterministic across source order");

// M8.7: Java source declarations are source evidence, not compiled code.
var javaFiles = new[]
{
    File("src/demo/Worker.java", """
        package demo.app;
        // class Ghost {}
        public class Worker {
            private int value;
            public Worker(int value) { this.value = value; }
            public void run() {}
            public String label() { return "class Phantom {}"; }
            class Inner {
                void ping() {}
            }
        }
        interface Service {
            void save();
        }
        enum Mode {
            A, B;
        }
        """),
    File("src/demo/Other.java", """
        package demo.app;
        public class Other {}
        """)
};
var javaGraph = CodeAtlasIndexer.Index("java-run", javaFiles);
Check(javaGraph.Coverage == "JAVA_STRUCTURE_ONLY" &&
    javaGraph.Diagnostics.Count == 0,
    "Java-only source is explicitly structural, not executable or semantic");
Check(javaGraph.Nodes.Count(n => n.Kind == "Namespace" &&
    n.Name == "demo.app") == 1,
    "Java package is a stable, shared source-backed namespace");
var javaWorker = javaGraph.Nodes.Single(n =>
    n.Kind == "Class" && n.Name == "Worker");
Check(javaWorker.Line == 3 &&
    javaWorker.Path == "src/demo/Worker.java",
    "Java class is bound to exact source file and line");
var javaCtor = javaGraph.Nodes.Single(n =>
    n.Kind == "Constructor" &&
    n.Name.StartsWith("Worker(", StringComparison.Ordinal));
Check(javaCtor.ParentId == javaWorker.Id && javaCtor.Line == 5,
    "Java constructor is attached to its declaring class");
Check(javaGraph.Nodes.Any(n => n.Kind == "Method" &&
    n.Name.StartsWith("run(", StringComparison.Ordinal) &&
    n.ParentId == javaWorker.Id && n.Line == 6),
    "Java method is declared with source line and owning class");
var javaInner = javaGraph.Nodes.Single(n =>
    n.Kind == "Class" && n.Name == "Inner");
Check(javaInner.ParentId == javaWorker.Id &&
    javaGraph.Nodes.Any(n =>
        n.Kind == "Method" &&
        n.Name.StartsWith("ping(", StringComparison.Ordinal) &&
        n.ParentId == javaInner.Id),
    "nested Java class and its method retain source containment");
Check(javaGraph.Nodes.Any(n => n.Kind == "Interface" &&
    n.Name == "Service") &&
    javaGraph.Nodes.Any(n => n.Kind == "Enum" && n.Name == "Mode") &&
    javaGraph.Nodes.Any(n => n.Kind == "Method" &&
        n.Name.StartsWith("save(", StringComparison.Ordinal)),
    "Java interfaces, enums, and interface member declarations are indexed");
Check(!javaGraph.Nodes.Any(n =>
    n.Name is "Ghost" or "Phantom") &&
    !javaGraph.Edges.Any(e =>
        e.Kind is "Calls" or "Inherits" or "Implements" or
        "ConstructorDependsOn"),
    "Java comment/string decoys and unresolved relationships stay absent");
Check(javaGraph.Edges.All(e =>
    javaGraph.Nodes.Any(n => n.Id == e.SourceId) &&
    javaGraph.Nodes.Any(n => n.Id == e.TargetId)),
    "Java graph contains no dangling source edges");
var javaReordered = CodeAtlasIndexer.Index("java-run",
    javaFiles.Reverse().ToArray());
Check(javaReordered.SourceRevision == javaGraph.SourceRevision &&
    javaReordered.Nodes.Select(n => n.Id).SequenceEqual(
        javaGraph.Nodes.Select(n => n.Id)),
    "Java nodes and revisions are deterministic across source order");
var pyJava = CodeAtlasIndexer.Index("mixed-py-java", new[]
{
    File("pkg/helper.py", "def helper(): pass"),
    File("pkg/Worker.java", "class Worker {}")
});
Check(pyJava.Coverage ==
    "PYTHON_STRUCTURE_ONLY+JAVA_STRUCTURE_ONLY" &&
    pyJava.Nodes.Any(n => n.Kind == "Function" && n.Name == "helper") &&
    pyJava.Nodes.Any(n => n.Kind == "Class" && n.Name == "Worker"),
    "Python and Java language coverage compose without invented linking");
var brokenJava = CodeAtlasIndexer.Index("broken-java", new[]
{
    File("app/Broken.java", """"
        public class Broken {
          String description = """
          class Fake {}
        """"),
    File("../escape.java", "public class Escaped {}")
});
Check(brokenJava.Coverage == "PARTIAL_JAVA_STRUCTURE" &&
    brokenJava.Diagnostics.Any(d =>
        d.StartsWith("JAVA_UNBALANCED_BRACES:", StringComparison.Ordinal)) &&
    brokenJava.Diagnostics.Any(d =>
        d.StartsWith("JAVA_UNCLOSED_TEXT_BLOCK:", StringComparison.Ordinal)),
    "unbalanced Java scopes and text blocks report partial coverage");
Check(brokenJava.Diagnostics.Contains("UNSAFE_PATH_EXCLUDED") &&
    !brokenJava.Nodes.Any(n => n.Name is "Fake" or "Escaped"),
    "untrusted Java paths and text-block decoys cannot enter the graph");

// M8.8: only literal HTML -> accepted JS/TS/CSS references are linked.
var markupFiles = new[]
{
    File("web/index.html", """
        <!doctype html>
        <html><head>
          <link rel="stylesheet" href="./styles.css">
          <link rel="stylesheet" href="https://cdn.example/x.css">
          <!-- <script src="./spoof.js"></script> -->
        </head><body>
          <script>const fake = '<script src="./spoof.js"></script>';</script>
          <script src="./main.js"></script>
          <script type="module" src="/web/app.tsx"></script>
          <script src="./missing.js"></script>
          <script src="../../outside.js"></script>
          <script src="./main.js" src="./spoof.js"></script>
        </body></html>
        """),
    File("web/main.js", "export function start() {}"),
    File("web/app.tsx", "export const App = () => null;"),
    File("web/styles.css", "body { color: red; }"),
    File("web/spoof.js", "export function Fake() {}")
};
var markupGraph = CodeAtlasIndexer.Index("markup-run", markupFiles);
var markupFileNodes = markupGraph.Nodes.Where(n =>
    n.Kind == "File" && n.Path is not null)
    .ToDictionary(n => n.Path!, n => n.Id);
var markupLinks = markupGraph.Edges.Where(e =>
    e.Kind == "MarkupReferences").ToArray();
Check(markupLinks.Length == 3 &&
    markupLinks.All(e =>
        e.SourceId == markupFileNodes["web/index.html"] &&
        e.Resolution == "LiteralMarkupReference" &&
        e.EvidencePath == "web/index.html" && e.EvidenceLine > 0),
    "exactly three saved HTML source references have source-line evidence");
Check(new[] { "web/styles.css", "web/main.js",
    "web/app.tsx" }.All(path =>
        markupLinks.Any(e => e.TargetId == markupFileNodes[path])),
    "literal CSS, JS and TSX targets resolve to existing indexed files");
Check(!markupLinks.Any(e =>
    e.TargetId == markupFileNodes["web/spoof.js"]),
    "comments, JS string decoys, and duplicate attributes never invent edges");
Check(markupGraph.Diagnostics.Any(d =>
    d.StartsWith("MARKUP_LOCAL_UNRESOLVED:", StringComparison.Ordinal)) &&
    markupGraph.Diagnostics.Any(d =>
    d.StartsWith("MARKUP_LOCAL_OUTSIDE_ROOT:", StringComparison.Ordinal)) &&
    markupGraph.Diagnostics.Any(d =>
    d.StartsWith("MARKUP_DUPLICATE_ATTRIBUTE:", StringComparison.Ordinal)) &&
    markupGraph.Coverage.EndsWith("+PARTIAL_MARKUP_STATIC_REFERENCES",
        StringComparison.Ordinal),
    "missing, escaping and ambiguous HTML references are diagnosed as partial");
var markupTrace = CodeAtlasPathFinder.Trace(markupGraph,
    markupFileNodes["web/index.html"],
    markupFileNodes["web/app.tsx"]);
Check(markupTrace.Found && markupTrace.Edges.Count == 1 &&
    markupTrace.Edges[0].Kind == "MarkupReferences",
    "read-only graph path tracing follows actual HTML to TSX source references");
var markupReordered = CodeAtlasIndexer.Index("markup-run",
    markupFiles.Reverse().ToArray());
Check(markupGraph.SourceRevision == markupReordered.SourceRevision &&
    markupGraph.Edges.Select(e => (e.Kind, e.SourceId, e.TargetId))
        .SequenceEqual(markupReordered.Edges.Select(e =>
            (e.Kind, e.SourceId, e.TargetId))),
    "cross-language references are deterministic regardless of source order");
var malformedMarkup = CodeAtlasIndexer.Index("markup-bad", new[]
{
    File("pages/index.html", "<!-- <script src='./missing.js'>"),
    File("../escape.html", "<script src='safe.js'></script>"),
    File("pages/safe.js", "export const ok = 1;")
});
Check(malformedMarkup.Coverage.EndsWith(
    "PARTIAL_MARKUP_STATIC_REFERENCES", StringComparison.Ordinal) &&
    malformedMarkup.Diagnostics.Any(d =>
        d.StartsWith("MARKUP_UNCLOSED_COMMENT:", StringComparison.Ordinal)) &&
    !malformedMarkup.Nodes.Any(n => n.Path == "../escape.html") &&
    !malformedMarkup.Edges.Any(e => e.Kind == "MarkupReferences"),
    "unsafe paths and incomplete HTML cannot produce imagined links");

// M8.2: source-only JS/TS declarations are never runtime/semantic facts.
var ecmaFiles = new[]
{
    File("ui/Screen.tsx", """
        // class NotReal { ghost() {} }
        const trap = "function Fabricated() { }";
        export interface IHandler { process(value: string): void; }
        export class Screen {
          constructor(handler: IHandler) {}
          public render(value: string): void {}
        }
        export function Start() { return null; }
        export const Panel = (props: Props) => <div />;
        """),
    File("ui/Helper.js", """
        export class Helper { run(value) {} }
        export const twice = n => n * 2;
        const pattern = /class Phantom/;
        """)
};
var ecmaGraph = CodeAtlasIndexer.Index("ecma-run", ecmaFiles);
Check(ecmaGraph.Coverage == "ECMA_STRUCTURE_ONLY" &&
    ecmaGraph.Diagnostics.Count == 0,
    "JS/TS coverage says structural only, without false semantic claims");
Check(ecmaGraph.Nodes.Any(n => n.Kind == "Interface" &&
    n.Name == "IHandler" && n.Path == "ui/Screen.tsx" && n.Line == 3),
    "TypeScript interface has precise saved source location");
Check(ecmaGraph.Nodes.Any(n => n.Kind == "Class" &&
    n.Name == "Screen" && n.Path == "ui/Screen.tsx"),
    "TSX class is indexed from source");
Check(ecmaGraph.Nodes.Any(n => n.Kind == "Constructor" &&
    n.Name.StartsWith("constructor(", StringComparison.Ordinal)),
    "TypeScript constructor is indexed as source structure");
Check(ecmaGraph.Nodes.Any(n => n.Kind == "Method" &&
    n.Name.StartsWith("render(", StringComparison.Ordinal)),
    "TypeScript class method is indexed without resolving calls");
Check(ecmaGraph.Nodes.Count(n => n.Kind == "Function") == 3,
    "named function plus two arrow functions recorded across TSX and JS");
Check(!ecmaGraph.Nodes.Any(n =>
    n.Name.Contains("NotReal", StringComparison.Ordinal) ||
    n.Name.Contains("Fabricated", StringComparison.Ordinal) ||
    n.Name.Contains("Phantom", StringComparison.Ordinal)),
    "comments, quoted strings and regexp literals never create fake nodes");
Check(!ecmaGraph.Edges.Any(e => e.Kind is "Calls" or "Inherits" or
    "Implements" or "ConstructorDependsOn"),
    "ECMAScript declarations never invent static semantic relationships");
Check(ecmaGraph.Edges.All(e =>
    ecmaGraph.Nodes.Any(n => n.Id == e.SourceId) &&
    ecmaGraph.Nodes.Any(n => n.Id == e.TargetId)),
    "ECMAScript graph has no dangling references");
var ecmaReordered = CodeAtlasIndexer.Index("ecma-run",
    ecmaFiles.Reverse().ToArray());
Check(ecmaGraph.SourceRevision == ecmaReordered.SourceRevision &&
    ecmaGraph.Nodes.Select(n => n.Id).SequenceEqual(
        ecmaReordered.Nodes.Select(n => n.Id)),
    "ECMAScript graph identities stable across source order");
var brokenEcma = CodeAtlasIndexer.Index("ecma-run", new[]
{
    File("ui/Broken.ts", "export class Broken { doWork() {"),
    File("../escape.ts", "export class Escape {}")
});
Check(brokenEcma.Coverage == "PARTIAL_ECMA_STRUCTURE" &&
    brokenEcma.Diagnostics.Any(d =>
        d.StartsWith("ECMA_UNBALANCED_BRACES:", StringComparison.Ordinal)),
    "unterminated JS/TS scopes report incomplete coverage");
Check(brokenEcma.Diagnostics.Contains("UNSAFE_PATH_EXCLUDED") &&
    !brokenEcma.Nodes.Any(n => n.Name == "Escape"),
    "untrusted JS/TS paths are rejected before parsing");

// M8.4: literal local imports resolve only to indexed files, not runtime calls.
var moduleSources = new[]
{
    File("src/main.tsx", """
        import { View } from './components/View';
        import './init';
        export { Helper } from '../shared/Helper';
        import config from './config.json';
        import pkg from 'react';
        import { Maybe } from './ambiguous';
        import { Missing } from './missing';
        import { Escaped } from '../../outside';
        const decoy = "import './spoof'";
        // import './spoof';
        const dynamic = import('./spoof');
        const pattern = /import '.\/spoof'/;
        """),
    File("src/components/View.tsx", "export class View { render() {} }"),
    File("src/init.js", "export function initialize() {}"),
    File("shared/Helper/index.js", "export function Helper() {}"),
    File("src/config.json", "{\"name\":\"fixture\"}"),
    File("src/ambiguous.ts", "export const alpha = 1;"),
    File("src/ambiguous.js", "export const beta = 1;"),
    File("src/spoof.ts", "export function Spoof() {}")
};
var moduleGraph = CodeAtlasIndexer.Index("module-run", moduleSources);
var moduleNodes = moduleGraph.Nodes.Where(n => n.Kind == "File" &&
    n.Path is not null).ToDictionary(n => n.Path!, n => n.Id);
var imports = moduleGraph.Edges.Where(e => e.Kind == "ModuleImports").ToArray();
Check(imports.Length == 4 &&
    imports.All(e => e.SourceId == moduleNodes["src/main.tsx"] &&
        e.Resolution == "LiteralLocalModule" &&
        e.EvidencePath == "src/main.tsx" && e.EvidenceLine > 0),
    "only four literal local ESM dependencies become source-backed edges");
Check(new[] { "src/components/View.tsx", "src/init.js",
    "shared/Helper/index.js", "src/config.json" }.All(path =>
    imports.Any(e => e.TargetId == moduleNodes[path])),
    "relative imports resolve existing files and unambiguous index modules");
Check(!imports.Any(e => e.TargetId == moduleNodes["src/spoof.ts"] ||
    e.TargetId == moduleNodes["src/ambiguous.ts"] ||
    e.TargetId == moduleNodes["src/ambiguous.js"]),
    "string/comment/regex/dynamic decoys and ambiguous paths invent no links");
Check(moduleGraph.Diagnostics.Any(d => d.StartsWith(
    "ECMA_LOCAL_IMPORT_UNRESOLVED:", StringComparison.Ordinal)) &&
    moduleGraph.Diagnostics.Any(d => d.StartsWith(
    "ECMA_LOCAL_IMPORT_AMBIGUOUS:", StringComparison.Ordinal)) &&
    moduleGraph.Diagnostics.Any(d => d.StartsWith(
    "ECMA_LOCAL_IMPORT_OUTSIDE_ROOT:", StringComparison.Ordinal)),
    "unresolved, ambiguous and escaping module references are diagnosed");
var tracedImport = CodeAtlasPathFinder.Trace(moduleGraph,
    moduleNodes["src/main.tsx"], moduleNodes["src/components/View.tsx"]);
Check(tracedImport.Found && tracedImport.Edges.Count == 1 &&
    tracedImport.Edges[0].Kind == "ModuleImports",
    "bounded read-only path tracing follows an actual local import edge");
var moduleReordered = CodeAtlasIndexer.Index("module-run",
    moduleSources.Reverse().ToArray());
Check(moduleGraph.SourceRevision == moduleReordered.SourceRevision &&
    moduleGraph.Edges.Select(e => (e.Kind, e.SourceId, e.TargetId))
        .SequenceEqual(moduleReordered.Edges.Select(e =>
            (e.Kind, e.SourceId, e.TargetId))),
    "module edges and revisions are stable across source-file order");

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
var indirect = CodeAtlasPathFinder.Trace(semantic, orderService, saveContract,
    maxHops: 3);
Check(indirect.Found && indirect.NodeIds.First() == orderService &&
    indirect.NodeIds.Last() == saveContract && indirect.Edges.Count >= 2,
    "constructor dependency plus interface declaration yields a bounded path");
Check(!CodeAtlasPathFinder.Trace(semantic, orderService, saveContract,
    maxHops: 1).Found,
    "hop limit rejects multi-edge relationships");
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
            CREATE TABLE engineering_atlas_snapshots (
                run_id TEXT NOT NULL, sequence INTEGER NOT NULL,
                source_revision TEXT NOT NULL,
                graph_json TEXT NOT NULL,
                PRIMARY KEY(run_id,sequence),
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
        cmd.CommandText = "SELECT COUNT(*) FROM engineering_atlas_snapshots";
        Check(Convert.ToInt32(cmd.ExecuteScalar()) == 2,
            "both committed graph checkpoints survive restart without rollback");
        cmd.CommandText = """
            SELECT graph_json FROM engineering_atlas_snapshots
            WHERE sequence=1
            """;
        var historic = (string?)cmd.ExecuteScalar() ?? "";
        Check(historic.Contains("Example.cs", StringComparison.Ordinal) &&
            !historic.Contains("Go()", StringComparison.Ordinal) &&
            !historic.Contains("namespace Demo;", StringComparison.Ordinal),
            "first graph checkpoint keeps its old structure but no source text");
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
    // Build the historical fixture from its own independently scoped source.
    // The event revision MUST match the persisted graph at this checkpoint.
    const string replaySource = "namespace Demo; public class Example { }";
    var checkpoint = CodeAtlasIndexer.Index(secondRun,
        new[] { File("Example.cs", replaySource) });
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
            VALUES('{secondRun}',1,'W1','{checkpoint.SourceRevision}','[]',
                '2026-11-22T00:00:00Z');
            """;
        cmd.ExecuteNonQuery();
    }
    using (var db = new SqliteConnection("Data Source=" +
        Path.Combine(privateDirectory, "codingassistant.db") + ";Pooling=False"))
    {
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO engineering_atlas_snapshots(
                run_id,sequence,source_revision,graph_json)
            VALUES($run,1,$revision,$graph)
            """;
        cmd.Parameters.AddWithValue("$run", secondRun);
        cmd.Parameters.AddWithValue("$revision", checkpoint.SourceRevision);
        cmd.Parameters.AddWithValue("$graph",
            System.Text.Json.JsonSerializer.Serialize(checkpoint));
        cmd.ExecuteNonQuery();
    }
    var timeline = ledger.Timeline(secondTask, secondRun);
    Check(timeline.Frames.Count == 1 &&
        timeline.Frames.Single().Sequence == 1,
        "history lists only committed available graph checkpoints");
    var replayed = ledger.Replay(secondTask, secondRun, 1);
    Check(replayed.Graph.SourceRevision == checkpoint.SourceRevision &&
        replayed.Graph.Nodes.Select(n => n.Id).SequenceEqual(
            checkpoint.Nodes.Select(n => n.Id)),
        "historical graph can be reconstructed exactly after restart");
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
    try { _ = ledger.Replay(firstTask, secondRun, 1); }
    catch (StoreFailure error) when (error.Code == "NOT_FOUND") { denied = true; }
    Check(denied, "historical snapshots deny cross-project access");
    denied = false;
    try { _ = ledger.Replay(secondTask, secondRun, 99); }
    catch (StoreFailure error) when (error.Code == "NOT_FOUND") { denied = true; }
    Check(denied, "unrecorded history is never reconstructed from current source");
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


// M6: map static QA findings to their exact source file; no test success claims.
var ownerTask = new string('e', 32);
var ownerRun = new string('f', 32);
var qaId = new string('a', 32);
var overlayFiles = new[] { File("Demo/Service.cs",
    "namespace Demo; public class Service { public void Save() {} }") };
var ownerRunRecord = new EngineeringRun(ownerRun, ownerTask, 1,
    new string('1',64), 1, new string('2',64),
    new string('3',64), "local", "Generated", "SourceOnlyGenerated",
    null, "2026-11-22T00:00:00Z", "2026-11-22T00:00:00Z");
var ownerSource = new EngineeringRunSnapshot(ownerRunRecord,
    Array.Empty<EngineeringAssignedTask>(),
    Array.Empty<EngineeringRunEvent>(), overlayFiles);
var ownerGraph = CodeAtlasIndexer.Index(ownerSource);
var qaFinding = new QaFinding("Major", "Save persistence",
    "Service.Save has no storage call", "Implement persistence");
var qaRunRecord = new QaReviewRun(qaId, ownerTask, ownerRun,
    QaReviewStore.Fingerprint(overlayFiles), 1,
    new string('1',64), 1, new string('2',64),
    new string('3',64), "local", "Reviewed", "StaticReviewOnly",
    null, "2026-11-22T00:00:00Z", "2026-11-22T00:00:00Z");
var qaReports = new[] { new QaFileReport(overlayFiles[0].Path,
    overlayFiles[0].Sha256, new QaFileAssessment("Unverified static review",
        new[] { qaFinding }, new[] { "Manually test Save" }),
    "2026-11-22T00:00:00Z") };
var reviewed = new QaReviewSnapshot(qaRunRecord, qaReports,
    Array.Empty<QaReviewEvent>(), "Generated");
var repair = new RepairCycle(new string('b',32), ownerTask,
    new string('c',32), ownerRun, new string('d',32),
    null, qaRunRecord.SourceSha256, "Generated", 1,
    null, new[] { new RepairFindingReference("Demo/Service.cs",0,"Major",
        qaFinding.Requirement,qaFinding.Evidence,
        qaFinding.Recommendation,"NeedsVerification") },
    "2026-11-22T00:00:00Z", "2026-11-22T00:00:00Z");
var overlay = CodeAtlasEvidenceProjector.Project(ownerSource,
    ownerGraph, reviewed, new[] { repair });
var fileNodeId = ownerGraph.Nodes.Single(n => n.Kind == "File").Id;
Check(overlay.VerificationStatus == "NOT_TESTED" &&
    overlay.QaReviewStatus == "STATIC_REVIEW_ONLY",
    "completed MAF QA static review never claims passed tests");
Check(overlay.Findings.Count == 1 &&
    overlay.Findings[0].FileNodeId == fileNodeId &&
    overlay.Findings[0].Classification == "UNVERIFIED_QA_HYPOTHESIS",
    "hypothesis attaches only to the exact existing file node");
Check(overlay.Repairs.Count == 1 &&
    overlay.Repairs[0].FileNodeIds.Contains(fileNodeId) &&
    overlay.Repairs[0].State == "Generated",
    "repair attempt is attached but never treated as resolved verification");

var mismatchedReport = reviewed with {
    Reports = new[] { qaReports[0] with { SourceSha256 = "different" } }
};
var staleMarker = CodeAtlasEvidenceProjector.Project(ownerSource,
    ownerGraph, mismatchedReport, Array.Empty<RepairCycle>());
Check(staleMarker.Findings.Count == 0 &&
    staleMarker.Diagnostics.Contains("QA_FILE_EVIDENCE_MISMATCH:Demo/Service.cs"),
    "report from another file hash cannot label current graph");
var otherReview = reviewed with {
    Job = qaRunRecord with { EngineeringRunId = new string('9',32) }
};
var otherOverlay = CodeAtlasEvidenceProjector.Project(ownerSource,
    ownerGraph, otherReview, Array.Empty<RepairCycle>());
Check(otherOverlay.Findings.Count == 0 &&
    otherOverlay.QaReviewStatus == "NOT_REVIEWED" &&
    otherOverlay.Diagnostics.Contains("QA_REVIEW_FOR_DIFFERENT_SOURCE_REVISION"),
    "other-run QA findings are hidden from the current source graph");
var wrongRepair = repair with { TaskId = new string('0', 32) };
var scoped = CodeAtlasEvidenceProjector.Project(ownerSource,
    ownerGraph, reviewed, new[] { wrongRepair });
Check(scoped.Repairs.Count == 0,
    "foreign-task repair cycles cannot appear on the current graph");
bool invalidOverlay = false;
try
{
    _ = CodeAtlasEvidenceProjector.Project(ownerSource,
        ownerGraph with { RunId = new string('8', 32) }, reviewed, []);
}
catch (StoreFailure error) when (error.Code == "INVALID_INPUT")
{
    invalidOverlay = true;
}
Check(invalidOverlay, "graph and engineering-run mismatch is rejected");


// M6: the actual MAF QA tools cannot read another file or mutate source.
var otherFile = File("Demo/Private.cs",
    "namespace Demo; public class PrivateDetails {}");
var atlasTools = new CodeAtlasAgentReadOnlyTools(ownerRun,
    overlayFiles.Concat(new[] { otherFile }).ToArray(),
    "Demo/Service.cs");
Check(atlasTools.FindSymbols("Service").Any(n => n.Kind == "Class" &&
    n.Name == "Service"), "QA tool finds declared local symbols");
Check(atlasTools.FindSymbols("PrivateDetails").Count == 0,
    "QA tool cannot search another file's private source symbols");
Check(atlasTools.ListFileSymbols().All(n => n.Path == "Demo/Service.cs") &&
    atlasTools.ListFileSymbols().Count <= 16,
    "host-provided QA graph context is bounded and file-scoped");
Check(atlasTools.FindSymbols(new string('a',81)).Count == 0,
    "QA symbol queries are bounded");
var ownClass = atlasTools.FindSymbols("Service").Single(n =>
    n.Kind == "Class");
Check(atlasTools.GetRelationships(ownClass.Id).All(e =>
    e.RelatedSymbolId != string.Empty),
    "QA relationship results contain only known local symbol IDs");
var excerpt = atlasTools.GetSourceExcerpt(ownClass.Id);
Check(excerpt is not null && excerpt.Path == "Demo/Service.cs" &&
    excerpt.Excerpt.Contains("class Service", StringComparison.Ordinal),
    "QA source excerpt is bounded to the reviewed file");
var privateId = CodeAtlasIndexer.Index(ownerRun,
    overlayFiles.Concat(new[] { otherFile }).ToArray())
    .Nodes.Single(n => n.Kind == "Class" && n.Name == "PrivateDetails").Id;
Check(atlasTools.GetSourceExcerpt(privateId) is null &&
    atlasTools.GetRelationships(privateId).Count == 0,
    "other-file IDs cannot bypass the tool's own source boundary");
Check(atlasTools.GetSourceExcerpt(ownClass.Id)!.Excerpt.Length <= 1200,
    "QA source excerpt has a strict size limit");


// M8: architecture drift reads exact source revisions and never claims tests.
var driftOldSource = CodeAtlasIndexer.Index("architecture-run", new[]
{
    File("Catalog.cs", "namespace Drift; public class Catalog { public void Existing() {} }")
});
var driftNewSource = CodeAtlasIndexer.Index("architecture-run", new[]
{
    File("Catalog.cs", "namespace Drift; public class Catalog { public void Existing() {} public void Added() { Existing(); } }")
});
var driftFrom = new AtlasReplayFrame(1, "W1", driftOldSource.SourceRevision,
    "2026-11-22T00:00:00Z", driftOldSource);
var driftTo = new AtlasReplayFrame(2, "W2", driftNewSource.SourceRevision,
    "2026-11-22T00:00:01Z", driftNewSource);
var report = CodeAtlasArchitectureDrift.Compare(driftFrom, driftTo);
Check(report.RunId == "architecture-run" &&
    report.FromRevision == driftOldSource.SourceRevision &&
    report.ToRevision == driftNewSource.SourceRevision,
    "architecture drift binds exact run and historical source revisions");
Check(report.AddedEntities == 1 && report.RemovedEntities == 0 &&
    report.Changes.Any(c => c.Operation == "Added" && c.Kind == "Method" &&
        c.Identity.Contains("Added()", StringComparison.Ordinal)),
    "architecture drift reports a newly persisted method, not source narration");
Check(report.AddedRelationships == 1 &&
    report.Changes.Any(c => c.Kind == "Calls" &&
        c.Resolution == "ResolvedStatic"),
    "architecture drift reports only resolved static method call evidence");
var driftUnchanged = CodeAtlasArchitectureDrift.Compare(driftFrom,
    driftTo with { Graph = driftOldSource,
        SourceRevision = driftOldSource.SourceRevision });
Check(driftUnchanged.Changes.Count == 0,
    "identical checkpoints have no invented architecture drift");
var rejectedDrift = false;
try { _ = CodeAtlasArchitectureDrift.Compare(driftTo, driftFrom); }
catch (StoreFailure e) when (e.Code == "INVALID_INPUT") { rejectedDrift = true; }
Check(rejectedDrift, "reverse historical comparison is rejected");
rejectedDrift = false;
try { _ = CodeAtlasArchitectureDrift.Compare(driftFrom,
    driftTo with { Graph = driftNewSource with { RunId = "other-run" } }); }
catch (StoreFailure e) when (e.Code == "INVALID_INPUT") { rejectedDrift = true; }
Check(rejectedDrift, "cross-run architecture comparison is rejected");
var manyNodes = Enumerable.Range(0, 125)
    .Select(i => new AtlasNode("class:" + i, "Class", "C" + i,
        "Drift.C" + i, null, "Drift.cs", i + 1)).ToArray();
var manyOld = new AtlasSnapshot("bounded-run", "old", "SYNTAX_ONLY",
    Array.Empty<AtlasNode>(), Array.Empty<AtlasEdge>(), Array.Empty<string>());
var manyNew = manyOld with { SourceRevision = "new", Nodes = manyNodes };
var limitedDrift = CodeAtlasArchitectureDrift.Compare(
    new AtlasReplayFrame(1, "W1", "old", "", manyOld),
    new AtlasReplayFrame(2, "W2", "new", "", manyNew));
Check(limitedDrift.AddedEntities == 125 && limitedDrift.Truncated &&
    limitedDrift.Changes.Count == 100,
    "architecture drift caps detailed payload without hiding total changes");

// M8.3: normalize import identity before diffing two saved commit graphs.
var oldImportId = new string('1', 32);
var newImportId = new string('2', 32);
var oldCommitGraph = CodeAtlasIndexer.Index(oldImportId, new[]
{
    File("src/Order.cs", "namespace Demo; public class Order { public void Save() {} }"),
    File("ui/View.ts", "export const View = () => 1;")
});
var newCommitGraph = CodeAtlasIndexer.Index(newImportId, new[]
{
    File("src/Order.cs", "namespace Demo; public class Order { public void Save() {} public void Load() { Save(); } }"),
    File("ui/View.ts", "export const View = () => 1;")
});
AtlasImportedRepositoryGraph ImportFixture(string id, string commit,
    AtlasSnapshot graph, string task = "task-one",
    string url = "https://github.com/example/Project") =>
    new(new AtlasImportedRepository(id, task, url, commit,
        graph.SourceRevision, graph.Coverage, 2, 0, ""), graph);
var oldImport = ImportFixture(oldImportId, new string('a',40), oldCommitGraph);
var newImport = ImportFixture(newImportId, new string('b',40), newCommitGraph);
var commitDrift = CodeAtlasImportedCommitDrift.Compare(oldImport, newImport);
Check(commitDrift.AddedEntities == 1 && commitDrift.RemovedEntities == 0 &&
    commitDrift.Changes.Any(c => c.Operation == "Added" &&
        c.Kind == "Method" && c.Identity.Contains("Load()", StringComparison.Ordinal)),
    "import commit drift sees new method, not unrelated import IDs");
Check(commitDrift.AddedRelationships == 1 &&
    commitDrift.Changes.Any(c => c.Kind == "Calls" &&
        c.Resolution == "ResolvedStatic"),
    "import commit drift retains source-backed C# resolved calls");
var sameGraph = CodeAtlasIndexer.Index(newImportId, new[]
{
    File("src/Order.cs", "namespace Demo; public class Order { public void Save() {} }"),
    File("ui/View.ts", "export const View = () => 1;")
});
var sameImport = ImportFixture(newImportId, new string('b',40), sameGraph);
Check(CodeAtlasImportedCommitDrift.Compare(oldImport, sameImport)
    .Changes.Count == 0,
    "unchanged source under random import IDs creates zero false drift");
var deniedComparison = false;
try { _ = CodeAtlasImportedCommitDrift.Compare(oldImport,
    newImport with { Repository = newImport.Repository with
        { TaskId = "different" } }); }
catch (StoreFailure e) when (e.Code == "ATLAS_IMPORT_DRIFT_INVALID")
{ deniedComparison = true; }
Check(deniedComparison, "import drift never crosses task boundaries");
deniedComparison = false;
try { _ = CodeAtlasImportedCommitDrift.Compare(oldImport,
    newImport with { Repository = newImport.Repository with
        { RepositoryUrl = "https://github.com/other/repo" } }); }
catch (StoreFailure e) when (e.Code == "ATLAS_IMPORT_DRIFT_INVALID")
{ deniedComparison = true; }
Check(deniedComparison, "import drift never compares different repositories");
deniedComparison = false;
try { _ = CodeAtlasImportedCommitDrift.Compare(oldImport, oldImport); }
catch (StoreFailure e) when (e.Code == "ATLAS_IMPORT_DRIFT_INVALID")
{ deniedComparison = true; }
Check(deniedComparison, "import drift rejects same-commit self-comparison");

// M7: GitHub archive parsing and source persistence are offline-only tests.
byte[] Archive(params (string Name, string Content, int Mode)[] entries)
{
    using var output = new MemoryStream();
    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
    {
        foreach (var (name, content, mode) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            if (mode != 0) entry.ExternalAttributes = mode << 16;
            using var writer = new StreamWriter(entry.Open(),
                new System.Text.UTF8Encoding(false));
            writer.Write(content);
        }
    }
    return output.ToArray();
}

void RejectedArchive(byte[] payload, string code)
{
    var denied = false;
    try { _ = CodeAtlasRepositoryArchive.Parse(payload); }
    catch (StoreFailure failure) when (failure.Code == code)
    { denied = true; }
    Check(denied, code + " import must be rejected");
}

var archiveBytes = Archive(
    ("Sample-root/Sample/App.csproj", "<Project/>", 0x81A4),
    ("Sample-root/Sample/Order.cs", """
        namespace Sample;
        public class Order { public void Place() {} }
        """, 0x81A4),
    ("Sample-root/Sample/client.ts", "export const task = 7;", 0x81A4),
    ("Sample-root/node_modules/dependency.js", "untrusted dependency", 0x81A4),
    ("Sample-root/Assets/image.png", "not source", 0x81A4));
var imported = CodeAtlasRepositoryArchive.Parse(archiveBytes);
Check(imported.Files.Count == 3 &&
    imported.ExcludedFiles == 2, "only source extensions are indexed");
Check(imported.Files.Any(f => f.Path == "Sample/Order.cs"),
    "repository root is stripped without changing source path");
Check(imported.Files.All(f => f.Sha256.Length == 64),
    "imported text hashes are available for exact identity");
var importGraph = CodeAtlasIndexer.Index("import-1", imported.Files);
Check(importGraph.Nodes.Any(n => n.Kind == "Class" && n.Name == "Order"),
    "imported C# source enters the existing graph pipeline");
Check(importGraph.Nodes.Any(n => n.Kind == "File" &&
    n.Path == "Sample/client.ts"),
    "unsupported semantic language still appears as a file");
RejectedArchive(Archive(("Sample/../private.cs", "secret", 0x81A4)),
    "ATLAS_ARCHIVE_PATH_INVALID");
RejectedArchive(Archive(("Sample/unsafe.cs", "secret", 0xA1FF)),
    "ATLAS_ARCHIVE_SYMLINK_REJECTED");
RejectedArchive(Archive(
    ("Sample/Same.cs", "class First {}", 0x81A4),
    ("Sample/same.cs", "class Second {}", 0x81A4)),
    "ATLAS_ARCHIVE_DUPLICATE_PATH");
RejectedArchive(Archive(
    ("Sample/Code.cs", "class A {}", 0x81A4),
    ("Other/Other.cs", "class B {}", 0x81A4)),
    "ATLAS_ARCHIVE_ROOT_MISMATCH");
RejectedArchive(Archive(("Sample/../../oops.cs", "bad", 0x81A4)),
    "ATLAS_ARCHIVE_PATH_INVALID");

var commitHash = new string('a', 40);
var validRepository = new AtlasImportGitHubRequest(
    "https://github.com/example/Project.git", commitHash);
var parsedGitHub = CodeAtlasRepositoryImportStore.ParseRequest(validRepository);
Check(parsedGitHub.Owner == "example" && parsedGitHub.Repo == "Project" &&
    parsedGitHub.Commit == commitHash,
    "exact public GitHub repository and SHA are parsed");
foreach (var candidate in new[] {
    "https://example.com/owner/Project",
    "http://github.com/owner/Project",
    "https://github.com@evil.example/owner/Project",
    "https://github.com/owner/Project?redirect=evil",
    "https://github.com/owner/Project/tree/main",
    "https://github.com/owner/../Project",
    "https://github.com/owner/Project#fragment"
})
{
    var denied = false;
    try
    {
        _ = CodeAtlasRepositoryImportStore.ParseRequest(
            new(candidate, commitHash));
    }
    catch (StoreFailure e) when (e.Code == "ATLAS_IMPORT_INPUT_INVALID")
    { denied = true; }
    Check(denied, "noncanonical user-supplied GitHub URL rejected: " + candidate);
}
var missingCommit = false;
try
{
    _ = CodeAtlasRepositoryImportStore.ParseRequest(
        new("https://github.com/example/Project", "main"));
}
catch (StoreFailure e) when (e.Code == "ATLAS_IMPORT_INPUT_INVALID")
{ missingCommit = true; }
Check(missingCommit, "unpinned GitHub branch is never accepted");

var importDirectory = Path.Combine(Path.GetTempPath(),
    "ca-atlas-import-" + Guid.NewGuid().ToString("N"));
try
{
    var tasksForImport = new TaskStore(importDirectory);
    var projectA = tasksForImport.CreateTask(new("Repository A",
        "Inspect a public GitHub source graph", new string('1',32)));
    var projectB = tasksForImport.CreateTask(new("Repository B",
        "Inspect a different repository", new string('2',32)));
    var fetcher = new AtlasFixtureDownloader(archiveBytes);
    var store = new CodeAtlasRepositoryImportStore(tasksForImport, fetcher);
    store.Initialize();
    var result = await store.ImportAsync(projectA.Id,
        new AtlasImportGitHubRequest(
            "https://github.com/example/Project", commitHash),
        CancellationToken.None);
    Check(result.TaskId == projectA.Id && result.IndexedFiles == 3 &&
        result.CommitSha == commitHash,
        "import is pinned to exact owner, project, commit and file count");
    Check(fetcher.DownloadCount == 1 && fetcher.Owner == "example" &&
        fetcher.Repo == "Project" && fetcher.CommitSha == commitHash,
        "only whitelisted repository segments reach the network boundary");
    var repeat = await store.ImportAsync(projectA.Id,
        new AtlasImportGitHubRequest(
            "https://github.com/example/Project", commitHash),
        CancellationToken.None);
    Check(repeat.Id == result.Id && fetcher.DownloadCount == 1,
        "duplicate pinned import is idempotent");
    var saved = store.Get(projectA.Id, result.Id);
    Check(saved.Graph.SourceRevision == result.SourceRevision &&
        saved.Graph.Nodes.Any(n => n.Kind == "Class" && n.Name == "Order"),
        "graph reload is bound to immutable commit and revision");
    var savedFile = store.GetFile(projectA.Id, result.Id,
        "Sample/Order.cs");
    Check(savedFile.Content.Contains("void Place()", StringComparison.Ordinal) &&
        savedFile.Sha256.Length == 64,
        "imported file source is available via authenticated project scope");
    bool denied = false;
    try { _ = store.Get(projectB.Id, result.Id); }
    catch (StoreFailure e) when (e.Code == "NOT_FOUND") { denied = true; }
    Check(denied, "other project cannot see an imported graph");
    denied = false;
    try { _ = store.GetFile(projectB.Id, result.Id, "Sample/Order.cs"); }
    catch (StoreFailure e) when (e.Code == "NOT_FOUND") { denied = true; }
    Check(denied, "other project cannot read imported source");
    denied = false;
    try { _ = store.GetFile(projectA.Id, result.Id, "../private.cs"); }
    catch (StoreFailure e) when (e.Code == "NOT_FOUND") { denied = true; }
    Check(denied, "import file retrieval never escapes the saved source set");
    Check(store.List(projectA.Id).Count == 1 &&
        store.List(projectB.Id).Count == 0,
        "import directory is project-scoped");
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(importDirectory))
        Directory.Delete(importDirectory, true);
}

// M7 authenticated HTTP acceptance with a fake, non-network archive backend.
// This proves the real owner-token API wiring, scoped import and file access.
var importHttpDirectory = Path.Combine(Path.GetTempPath(),
    "ca-atlas-import-http-" + Guid.NewGuid().ToString("N"));
var ownerTokenForHttp = new string('f', 64);
var apiFetcher = new AtlasFixtureDownloader(archiveBytes);
var httpApp = ApiHost.Build(Array.Empty<string>(),
    new ApiSettings(importHttpDirectory, ownerTokenForHttp,
        "http://127.0.0.1:0"),
    atlasArchiveOverride: apiFetcher);
try
{
    await httpApp.StartAsync();
    var bound = httpApp.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault() ??
        throw new InvalidOperationException("Atlas HTTP fixture missing loopback listener");
    Check(bound.StartsWith("http://127.0.0.1:", StringComparison.Ordinal),
        "import server remains on IPv4 loopback");
    using var client = new HttpClient { BaseAddress = new Uri(bound) };
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", ownerTokenForHttp);
    var storeForHttp = new TaskStore(importHttpDirectory);
    var taskA = storeForHttp.CreateTask(new("Import A",
        "Inspect public commit source", new string('3',32)));
    var taskB = storeForHttp.CreateTask(new("Import B",
        "Other user's source scope", new string('4',32)));
    using var upload = await client.PostAsJsonAsync(
        "/api/tasks/" + taskA.Id + "/atlas-imports",
        new AtlasImportGitHubRequest(
            "https://github.com/example/Project", commitHash));
    Check(upload.StatusCode == HttpStatusCode.OK,
        "authenticated read-only GitHub import HTTP route works");
    var received = await upload.Content.ReadFromJsonAsync<AtlasImportedRepository>();
    Check(received is not null &&
        received.TaskId == taskA.Id && received.CommitSha == commitHash,
        "import response includes task, commit and durable imported graph identity");
    var importedId = received!.Id;
    using var graphResponse = await client.GetAsync(
        "/api/tasks/" + taskA.Id + "/atlas-imports/" + importedId);
    Check(graphResponse.StatusCode == HttpStatusCode.OK,
        "authorized task can load the saved graph over HTTP");
    var graphPayload = await graphResponse.Content
        .ReadFromJsonAsync<AtlasImportedRepositoryGraph>();
    Check(graphPayload?.Graph.Nodes.Any(n =>
        n.Kind == "Class" && n.Name == "Order") == true,
        "HTTP graph endpoint returns actual imported C# symbols");
    using var sourceResponse = await client.GetAsync(
        "/api/tasks/" + taskA.Id + "/atlas-imports/" +
        importedId + "/file?path=Sample%2FOrder.cs");
    Check(sourceResponse.StatusCode == HttpStatusCode.OK,
        "committed source file is available from its owner-scoped endpoint");
    var fileResponse = await sourceResponse.Content
        .ReadFromJsonAsync<AtlasImportedFile>();
    Check(fileResponse?.Content.Contains("void Place()",
        StringComparison.Ordinal) == true,
        "HTTP source retrieval is bound to the imported file content");
    using var crossScope = await client.GetAsync(
        "/api/tasks/" + taskB.Id + "/atlas-imports/" + importedId);
    Check(crossScope.StatusCode == HttpStatusCode.NotFound,
        "cross-project imported graph ID never reveals private source");
    using var crossFile = await client.GetAsync(
        "/api/tasks/" + taskB.Id + "/atlas-imports/" +
        importedId + "/file?path=Sample%2FOrder.cs");
    Check(crossFile.StatusCode == HttpStatusCode.NotFound,
        "cross-project imported file access is rejected");
    using var unknown = await client.PostAsJsonAsync(
        "/api/tasks/" + taskA.Id + "/atlas-imports",
        new AtlasImportGitHubRequest(
            "https://example.com/owner/Project", commitHash));
    Check(unknown.StatusCode == HttpStatusCode.BadRequest,
        "off-domain repository URL rejected before network calls");
    using var unauthenticated = new HttpClient {
        BaseAddress = new Uri(bound)
    };
    using var forbidden = await unauthenticated.GetAsync(
        "/api/tasks/" + taskA.Id + "/atlas-imports/" + importedId);
    Check(forbidden.StatusCode == HttpStatusCode.Unauthorized,
        "imported graph requires a real owner bearer token");
    // Compare two already-imported commits via owner-authenticated HTTP.
    using var secondUpload = await client.PostAsJsonAsync(
        "/api/tasks/" + taskA.Id + "/atlas-imports",
        new AtlasImportGitHubRequest(
            "https://github.com/example/Project", new string('b',40)));
    Check(secondUpload.StatusCode == HttpStatusCode.OK,
        "second exact commit can be imported for architecture comparison");
    var secondImport = await secondUpload.Content
        .ReadFromJsonAsync<AtlasImportedRepository>();
    Check(secondImport is not null && secondImport.Id != importedId,
        "saved commit snapshots retain independent identities");
    var comparisonUrl = "/api/tasks/" + taskA.Id +
        "/atlas-imports/drift?from=" + importedId +
        "&to=" + secondImport!.Id;
    using var comparisonResponse = await client.GetAsync(comparisonUrl);
    Check(comparisonResponse.StatusCode == HttpStatusCode.OK,
        "read-only imported comparison endpoint is available");
    var comparison = await comparisonResponse.Content
        .ReadFromJsonAsync<AtlasImportedCommitDriftReport>();
    Check(comparison?.AddedEntities == 0 &&
        comparison.RemovedEntities == 0 &&
        comparison.AddedRelationships == 0,
        "same archived code produces no phantom import architecture changes");
    using var forbiddenComparison = await unauthenticated.GetAsync(comparisonUrl);
    Check(forbiddenComparison.StatusCode == HttpStatusCode.Unauthorized,
        "import architecture comparison requires an owner token");
    using var otherTaskComparison = await client.GetAsync(
        "/api/tasks/" + taskB.Id + "/atlas-imports/drift?from=" +
        importedId + "&to=" + secondImport.Id);
    Check(otherTaskComparison.StatusCode == HttpStatusCode.NotFound,
        "other task cannot read imported source drift");
    Check(apiFetcher.DownloadCount == 2,
        "comparing saved commits never downloads a third archive");
}
finally
{
    await httpApp.StopAsync();
    await httpApp.DisposeAsync();
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(importHttpDirectory))
        Directory.Delete(importHttpDirectory, true);
}

Console.WriteLine("CODE_ATLAS_SOURCE_GRAPH_PASS: " + checks + " assertions");
sealed class AtlasFixtureDownloader(byte[] archive)
    : IAtlasCommitArchiveDownloader
{
    public int DownloadCount { get; private set; }
    public string Owner { get; private set; } = "";
    public string Repo { get; private set; } = "";
    public string CommitSha { get; private set; } = "";

    public Task<byte[]> DownloadAsync(string owner, string repo,
        string commitSha, CancellationToken cancellation)
    {
        DownloadCount++;
        Owner = owner;
        Repo = repo;
        CommitSha = commitSha;
        return Task.FromResult(archive);
    }
}

