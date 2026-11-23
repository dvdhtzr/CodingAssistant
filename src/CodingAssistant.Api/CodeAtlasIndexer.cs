using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodingAssistant.Api;

// A bounded, source-backed projection. This never loads a project, executes
// untrusted code, invokes MSBuild, restores dependencies, or reads disk files.
public sealed record AtlasNode(string Id, string Kind, string Name,
    string QualifiedName, string? ParentId, string? Path, int? Line);
public sealed record AtlasEdge(string SourceId, string TargetId, string Kind,
    string? EvidencePath = null, int? EvidenceLine = null,
    string Resolution = "Syntax");
public sealed record AtlasSnapshot(string RunId, string SourceRevision,
    string Coverage, IReadOnlyList<AtlasNode> Nodes,
    IReadOnlyList<AtlasEdge> Edges, IReadOnlyList<string> Diagnostics);

public static class CodeAtlasIndexer
{
    public const int MaximumNodes = 4000;
    public const int MaximumEdges = 8000;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    internal static string Id(string kind, string key) =>
        kind.ToLowerInvariant() + ":" + Hash(kind + "|" + key)[..24];

    private static bool ValidPath(string path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 240 &&
        !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') &&
        !path.Any(char.IsControl) &&
        path.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));

    private static string NamespaceOf(SyntaxNode node) =>
        string.Join(".", node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse().Select(n => n.Name.ToString()));

    private static string TypeName(BaseTypeDeclarationSyntax node) =>
        node.Identifier.ValueText + (node is TypeDeclarationSyntax t &&
            t.TypeParameterList is { } generics
            ? "#" + generics.Parameters.Count : "");

    private static string FullTypeName(BaseTypeDeclarationSyntax node)
    {
        var ns = NamespaceOf(node);
        var parents = node.Ancestors().OfType<BaseTypeDeclarationSyntax>()
            .Reverse().Append(node).Select(TypeName);
        return (ns.Length == 0 ? "" : ns + ".") + string.Join(".", parents);
    }

    private static string TypeKind(BaseTypeDeclarationSyntax node) => node switch
    {
        InterfaceDeclarationSyntax => "Interface",
        EnumDeclarationSyntax => "Enum",
        StructDeclarationSyntax => "Struct",
        RecordDeclarationSyntax record when
            record.ClassOrStructKeyword.ValueText == "struct" => "Struct",
        _ => "Class"
    };

    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    public static AtlasSnapshot Index(EngineeringRunSnapshot run) =>
        Index(run.Job.Id, run.Files);

    public static AtlasSnapshot Index(string runId,
        IReadOnlyList<EngineeringSourceFile> files,
        string rootLabel = "Generated source")
    {
        ArgumentNullException.ThrowIfNull(files);
        var nodes = new Dictionary<string, AtlasNode>(StringComparer.Ordinal);
        var edges = new Dictionary<string, AtlasEdge>(StringComparer.Ordinal);
        var diagnostics = new List<string>();

        string Node(string kind, string key, string name,
            string? parent = null, string? path = null, int? line = null)
        {
            var id = Id(kind, key);
            if (!nodes.ContainsKey(id))
            {
                if (nodes.Count >= MaximumNodes)
                {
                    if (!diagnostics.Contains("NODE_LIMIT"))
                        diagnostics.Add("NODE_LIMIT");
                    return "";
                }
                nodes.Add(id, new(id, kind, name, key, parent, path, line));
            }
            return id;
        }
        void Edge(string from, string to, string kind)
        {
            if (!nodes.ContainsKey(from) || !nodes.ContainsKey(to)) return;
            var key = kind + "|" + from + "|" + to;
            if (edges.Count >= MaximumEdges && !edges.ContainsKey(key))
            {
                if (!diagnostics.Contains("EDGE_LIMIT"))
                    diagnostics.Add("EDGE_LIMIT");
                return;
            }
            edges.TryAdd(key, new(from, to, kind));
        }

        var valid = files.Where(f => ValidPath(f.Path))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        if (valid.Length != files.Count)
            diagnostics.Add("UNSAFE_PATH_EXCLUDED");
        var revision = Hash(string.Join("\n",
            valid.Select(f => f.Path + "|" + Hash(f.Content))));
        var root = Node("Repository", runId, rootLabel);

        var projects = valid.Where(f => f.Path.EndsWith(".csproj",
            StringComparison.OrdinalIgnoreCase)).Select(f => f.Path).ToArray();
        foreach (var project in projects)
        {
            var p = Node("Project", project, project.Split('/').Last(),
                root, project, 1);
            Edge(root, p, "Contains");
        }

        foreach (var file in valid)
        {
            var project = projects.Where(p =>
            {
                var slash = p.LastIndexOf('/');
                return file.Path.StartsWith(slash < 0 ? "" : p[..(slash + 1)],
                    StringComparison.Ordinal);
            }).OrderByDescending(p => p.LastIndexOf('/'))
                .ThenBy(p => p, StringComparer.Ordinal).FirstOrDefault();
            var scope = project ?? runId;
            var parent = project is null ? root : Id("Project", project);
            var segments = file.Path.Split('/');
            var directory = "";
            foreach (var segment in segments.SkipLast(1))
            {
                directory += segment + "/";
                var folder = Node("Folder", scope + "|" + directory,
                    segment, parent);
                Edge(parent, folder, "Contains");
                parent = folder;
            }
            var fileId = Node("File", file.Path, segments[^1],
                parent, file.Path, 1);
            Edge(parent, fileId, "Contains");

            if (!file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            var tree = CSharpSyntaxTree.ParseText(file.Content, path: file.Path);
            var syntax = tree.GetRoot();
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                diagnostics.Add("INCOMPLETE_CSHARP:" + file.Path);

            foreach (var ns in syntax.DescendantNodes()
                .OfType<BaseNamespaceDeclarationSyntax>())
            {
                var qualifier = NamespaceOf(ns);
                var full = (qualifier.Length == 0 ? "" : qualifier + ".") +
                    ns.Name.ToString();
                var nsId = Node("Namespace", scope + "|" + full, full,
                    project is null ? root : Id("Project", project),
                    file.Path, Line(ns));
                Edge(project is null ? root : Id("Project", project),
                    nsId, "Contains");
                Edge(fileId, nsId, "Declares");
            }

            foreach (var type in syntax.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>())
            {
                var full = FullTypeName(type);
                var outer = type.Ancestors().OfType<BaseTypeDeclarationSyntax>()
                    .FirstOrDefault();
                var ns = NamespaceOf(type);
                var owner = outer is not null
                    ? Id(TypeKind(outer), scope + "|" + FullTypeName(outer))
                    : ns.Length > 0 ? Id("Namespace", scope + "|" + ns)
                    : project is null ? root : Id("Project", project);
                if (!nodes.ContainsKey(owner))
                    owner = project is null ? root : Id("Project", project);
                var typeId = Node(TypeKind(type), scope + "|" + full,
                    type.Identifier.ValueText, owner, file.Path, Line(type));
                Edge(owner, typeId, "Contains");
                Edge(fileId, typeId, "Declares");
            }

            foreach (var method in syntax.DescendantNodes()
                .OfType<BaseMethodDeclarationSyntax>())
            {
                if (method is not (MethodDeclarationSyntax or ConstructorDeclarationSyntax))
                    continue;
                var type = method.Ancestors().OfType<BaseTypeDeclarationSyntax>()
                    .FirstOrDefault();
                if (type is null) continue;
                var typeId = Id(TypeKind(type), scope + "|" + FullTypeName(type));
                var parameters = method switch
                {
                    MethodDeclarationSyntax m => m.ParameterList.Parameters,
                    ConstructorDeclarationSyntax c => c.ParameterList.Parameters,
                    _ => default
                };
                var methodName = method is MethodDeclarationSyntax declared
                    ? declared.Identifier.ValueText +
                        (declared.TypeParameterList is { } generic
                            ? "#" + generic.Parameters.Count : "")
                    : ".ctor";
                var signature = methodName + "(" + string.Join(",",
                    parameters.Select(p => p.Modifiers.ToString() + ":" +
                        p.Type?.ToString())) + ")";
                var kind = method is ConstructorDeclarationSyntax
                    ? "Constructor" : "Method";
                var member = Node(kind, scope + "|" + FullTypeName(type) +
                    "." + signature, signature, typeId, file.Path, Line(method));
                Edge(typeId, member, "Contains");
                Edge(fileId, member, "Declares");
            }
        }

        var coverage = diagnostics.Any(d =>
            d.StartsWith("INCOMPLETE_CSHARP:", StringComparison.Ordinal))
            ? "PARTIAL_SYNTAX" : "SYNTAX_ONLY";
        var syntaxGraph = new AtlasSnapshot(runId, revision, coverage,
            nodes.Values.OrderBy(n => n.Kind, StringComparer.Ordinal)
                .ThenBy(n => n.QualifiedName, StringComparer.Ordinal).ToArray(),
            edges.Values.OrderBy(e => e.Kind, StringComparer.Ordinal)
                .ThenBy(e => e.SourceId, StringComparer.Ordinal)
                .ThenBy(e => e.TargetId, StringComparer.Ordinal).ToArray(),
            diagnostics.AsReadOnly());
        var projectGraph = syntaxGraph;
        try
        {
            projectGraph = CodeAtlasProjectReferences.Enrich(syntaxGraph, valid);
        }
        catch (Exception)
        {
            projectGraph = syntaxGraph with
            {
                Diagnostics = syntaxGraph.Diagnostics.Concat(
                    new[] { "PROJECT_REFERENCE_ANALYSIS_UNAVAILABLE" }).ToArray()
            };
        }
        AtlasSnapshot semanticGraph;
        try
        {
            semanticGraph = CodeAtlasSemanticIndexer.Enrich(projectGraph, valid);
        }
        catch (Exception)
        {
            // A failure to resolve static symbols must never hide saved source.
            semanticGraph = projectGraph with
            {
                Diagnostics = projectGraph.Diagnostics.Concat(
                    new[] { "SEMANTIC_ANALYSIS_UNAVAILABLE" }).ToArray()
            };
        }
        AtlasSnapshot ecmaGraph;
        try
        {
            ecmaGraph = CodeAtlasEcmaIndexer.Enrich(semanticGraph, valid);
        }
        catch (Exception)
        {
            // Failed JS/TS projections must not roll back persisted source.
            ecmaGraph = semanticGraph with
            {
                Diagnostics = semanticGraph.Diagnostics.Concat(
                    new[] { "ECMA_STRUCTURE_ANALYSIS_UNAVAILABLE" }).ToArray()
            };
        }
        AtlasSnapshot pythonGraph;
        try
        {
            pythonGraph = CodeAtlasPythonIndexer.Enrich(ecmaGraph, valid);
        }
        catch (Exception)
        {
            // Failed indexing never rolls back an accepted source revision.
            pythonGraph = ecmaGraph with
            {
                Diagnostics = ecmaGraph.Diagnostics.Concat(
                    new[] { "PYTHON_STRUCTURE_ANALYSIS_UNAVAILABLE" }).ToArray()
            };
        }
        AtlasSnapshot javaGraph;
        try
        {
            javaGraph = CodeAtlasJavaIndexer.Enrich(pythonGraph, valid);
        }
        catch (Exception)
        {
            javaGraph = pythonGraph with
            {
                Diagnostics = pythonGraph.Diagnostics.Concat(
                    new[] { "JAVA_STRUCTURE_ANALYSIS_UNAVAILABLE" }).ToArray()
            };
        }
        try
        {
            return CodeAtlasMarkupIndexer.Enrich(javaGraph, valid);
        }
        catch (Exception)
        {
            return javaGraph with
            {
                Diagnostics = javaGraph.Diagnostics.Concat(
                    new[] { "MARKUP_REFERENCE_ANALYSIS_UNAVAILABLE" }).ToArray()
            };
        }
    }
}
