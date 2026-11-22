using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodingAssistant.Api;

/// <summary>
/// Static-only Roslyn enrichment of accepted source. Constructs in-memory
/// compilations against trusted framework metadata, without loading user
/// assemblies, restoring packages, evaluating MSBuild, emitting or executing.
/// Reflection and runtime dynamic dispatch remain outside static coverage.
/// </summary>
public static class CodeAtlasSemanticIndexer
{
    private static readonly SymbolDisplayFormat SymbolFormat =
        SymbolDisplayFormat.FullyQualifiedFormat;

    private static string Scope(string path, IReadOnlyList<string> projects,
        string runId)
    {
        var project = projects.Where(p =>
        {
            var slash = p.LastIndexOf('/');
            return path.StartsWith(slash < 0 ? "" : p[..(slash + 1)],
                StringComparison.Ordinal);
        }).OrderByDescending(p => p.LastIndexOf('/'))
          .ThenBy(p => p, StringComparer.Ordinal).FirstOrDefault();
        return project ?? runId;
    }

    private static string SymbolKey(ISymbol symbol) =>
        symbol.OriginalDefinition.ToDisplayString(SymbolFormat);

    private static string NameOf(INamedTypeSymbol symbol)
    {
        var parents = new Stack<string>();
        for (var current = symbol; current is not null;
            current = current.ContainingType)
            parents.Push(current.MetadataName.Replace((char)96, '#'));
        var ns = symbol.ContainingNamespace.IsGlobalNamespace ? "" :
            symbol.ContainingNamespace.ToDisplayString() + ".";
        return ns + string.Join(".", parents);
    }

    private static string Signature(BaseMethodDeclarationSyntax declaration)
    {
        var parameters = declaration switch
        {
            MethodDeclarationSyntax m => m.ParameterList.Parameters,
            ConstructorDeclarationSyntax c => c.ParameterList.Parameters,
            _ => default
        };
        var name = declaration is MethodDeclarationSyntax method
            ? method.Identifier.ValueText +
                (method.TypeParameterList is { } generics
                    ? "#" + generics.Parameters.Count : "")
            : ".ctor";
        return name + "(" + string.Join(",",
            parameters.Select(p => p.Modifiers.ToString() +
                ":" + p.Type?.ToString())) + ")";
    }

    public static AtlasSnapshot Enrich(AtlasSnapshot syntax,
        IReadOnlyList<EngineeringSourceFile> source)
    {
        var accepted = syntax.Nodes.Where(n => n.Kind == "File" &&
            n.Path is not null).Select(n => n.Path!)
            .ToHashSet(StringComparer.Ordinal);
        var projects = syntax.Nodes.Where(n => n.Kind == "Project" &&
            n.Path is not null).Select(n => n.Path!).ToArray();
        var csharp = source.Where(f => accepted.Contains(f.Path) &&
            f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (csharp.Length == 0) return syntax;

        var diagnostics = syntax.Diagnostics.ToList();
        var edges = syntax.Edges.ToDictionary(
            e => e.Kind + "|" + e.SourceId + "|" + e.TargetId,
            e => e, StringComparer.Ordinal);
        var nodes = syntax.Nodes.Where(n => n.Kind is
            "Class" or "Interface" or "Struct" or "Enum" or
            "Method" or "Constructor")
            .ToDictionary(n => n.QualifiedName, n => n,
                StringComparer.Ordinal);
        var successes = 0;
        var incomplete = false;

        void Edge(string sourceId, string targetId, string kind,
            string path, int line)
        {
            if (sourceId == targetId) return;
            var key = kind + "|" + sourceId + "|" + targetId;
            if (edges.ContainsKey(key)) return;
            if (edges.Count >= CodeAtlasIndexer.MaximumEdges)
            {
                if (!diagnostics.Contains("SEMANTIC_EDGE_LIMIT"))
                    diagnostics.Add("SEMANTIC_EDGE_LIMIT");
                return;
            }
            edges.Add(key, new(sourceId, targetId, kind,
                path, line, "ResolvedStatic"));
        }

        foreach (var group in csharp.GroupBy(f => Scope(f.Path,
            projects, syntax.RunId), StringComparer.Ordinal))
        {
            var paths = group.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
            var trees = paths.Select(file =>
                CSharpSyntaxTree.ParseText(file.Content, path: file.Path))
                .ToArray();
            if (trees.Any(tree => tree.GetDiagnostics().Any(d =>
                d.Severity == DiagnosticSeverity.Error)))
            {
                incomplete = true;
                diagnostics.Add("SEMANTIC_SKIPPED_INCOMPLETE:" + group.Key);
                continue;
            }

            // Never load assemblies from the source repository. These paths
            // refer only to already installed, host-trusted .NET runtime files.
            var references = new[]
            {
                typeof(object).Assembly.Location,
                typeof(Enumerable).Assembly.Location,
                typeof(System.Runtime.GCSettings).Assembly.Location
            }.Distinct(StringComparer.Ordinal)
             .Where(path => !string.IsNullOrWhiteSpace(path))
             .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
            var compilation = CSharpCompilation.Create("CodeAtlasStatic",
                trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var typeIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var methodIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var declaredMethods = new List<(IMethodSymbol Symbol, string NodeId)>();

            string? ResolveMethod(IMethodSymbol candidate)
            {
                var original = (candidate.ReducedFrom ?? candidate).OriginalDefinition;
                if (methodIds.TryGetValue(SymbolKey(original), out var id))
                    return id;
                // Use Roslyn's identity when display text differs for a
                // constructed, reduced or interface method.
                return declaredMethods.FirstOrDefault(item =>
                    SymbolEqualityComparer.Default.Equals(
                        item.Symbol.OriginalDefinition, original)).NodeId;
            }

            // Pass 1: register symbols across every source file in the scope.
            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree,
                    ignoreAccessibility: true);
                foreach (var type in tree.GetRoot().DescendantNodes()
                    .OfType<BaseTypeDeclarationSyntax>())
                {
                    var symbol = model.GetDeclaredSymbol(type) as INamedTypeSymbol;
                    if (symbol is null) continue;
                    var qualifier = group.Key + "|" + NameOf(symbol);
                    if (nodes.TryGetValue(qualifier, out var node) &&
                        node.Kind is "Class" or "Interface" or "Struct" or "Enum")
                        typeIds[SymbolKey(symbol)] = node.Id;
                }

                foreach (var method in tree.GetRoot().DescendantNodes()
                    .OfType<BaseMethodDeclarationSyntax>())
                {
                    if (method is not (MethodDeclarationSyntax or
                        ConstructorDeclarationSyntax)) continue;
                    var declared = model.GetDeclaredSymbol(method) as IMethodSymbol;
                    if (declared is null) continue;
                    var kind = method is ConstructorDeclarationSyntax
                        ? "Constructor" : "Method";
                    var qualifier = group.Key + "|" +
                        NameOf(declared.ContainingType) + "." + Signature(method);
                    if (nodes.TryGetValue(qualifier, out var node) &&
                        node.Kind == kind)
                    {
                        methodIds[SymbolKey(declared)] = node.Id;
                        declaredMethods.Add((declared, node.Id));
                    }
                }
            }

            // Pass 2: record only relationships with resolved local targets.
            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree,
                    ignoreAccessibility: true);
                var path = tree.FilePath;
                foreach (var type in tree.GetRoot().DescendantNodes()
                    .OfType<BaseTypeDeclarationSyntax>())
                {
                    var symbol = model.GetDeclaredSymbol(type) as INamedTypeSymbol;
                    if (symbol is null ||
                        !typeIds.TryGetValue(SymbolKey(symbol), out var from))
                        continue;
                    var line = type.GetLocation().GetLineSpan()
                        .StartLinePosition.Line + 1;
                    if (symbol.BaseType is { } baseType &&
                        typeIds.TryGetValue(SymbolKey(baseType), out var ancestor))
                        Edge(from, ancestor, "Inherits", path, line);
                    foreach (var iface in symbol.Interfaces)
                        if (typeIds.TryGetValue(SymbolKey(iface), out var implemented))
                            Edge(from, implemented, "Implements", path, line);
                }

                foreach (var ctor in tree.GetRoot().DescendantNodes()
                    .OfType<ConstructorDeclarationSyntax>())
                {
                    var symbol = model.GetDeclaredSymbol(ctor) as IMethodSymbol;
                    if (symbol is null ||
                        !typeIds.TryGetValue(SymbolKey(symbol.ContainingType),
                            out var from)) continue;
                    var line = ctor.GetLocation().GetLineSpan()
                        .StartLinePosition.Line + 1;
                    foreach (var parameter in symbol.Parameters)
                        if (typeIds.TryGetValue(SymbolKey(parameter.Type),
                            out var dependency))
                            Edge(from, dependency,
                                "ConstructorDependsOn", path, line);
                }

                foreach (var invocation in tree.GetRoot().DescendantNodes()
                    .OfType<InvocationExpressionSyntax>())
                {
                    var caller = invocation.Ancestors()
                        .OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
                    if (caller is null) continue;
                    var calling = model.GetDeclaredSymbol(caller) as IMethodSymbol;
                    if (calling is null) continue;
                    var from = ResolveMethod(calling);
                    if (from is null) continue;
                    var called = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                    if (called is null) continue;
                    var to = ResolveMethod(called);
                    if (to is null) continue;
                    Edge(from, to, "Calls", path,
                        invocation.GetLocation().GetLineSpan()
                            .StartLinePosition.Line + 1);
                }

                foreach (var creation in tree.GetRoot().DescendantNodes()
                    .OfType<ObjectCreationExpressionSyntax>())
                {
                    var caller = creation.Ancestors()
                        .OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
                    if (caller is null) continue;
                    var calling = model.GetDeclaredSymbol(caller) as IMethodSymbol;
                    if (calling is null) continue;
                    var from = ResolveMethod(calling);
                    if (from is null) continue;
                    var ctor = model.GetSymbolInfo(creation).Symbol as IMethodSymbol;
                    if (ctor is null) continue;
                    var to = ResolveMethod(ctor);
                    if (to is null) continue;
                    Edge(from, to, "Calls", path,
                        creation.GetLocation().GetLineSpan()
                            .StartLinePosition.Line + 1);
                }
            }
            successes++;
        }

        return syntax with
        {
            Coverage = incomplete || successes == 0
                ? successes == 0 ? syntax.Coverage : "PARTIAL_SEMANTIC"
                : "STATIC_SEMANTIC",
            Edges = edges.Values.OrderBy(e => e.Kind, StringComparer.Ordinal)
                .ThenBy(e => e.SourceId, StringComparer.Ordinal)
                .ThenBy(e => e.TargetId, StringComparer.Ordinal).ToArray(),
            Diagnostics = diagnostics.AsReadOnly()
        };
    }
}
