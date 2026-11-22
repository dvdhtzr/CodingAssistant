using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

/// <summary>
/// M8.7: bounded Java source declaration projection in the API host.
/// Parses only persisted text. Never launches javac, builds, restores
/// dependencies, resolves the classpath or claims runtime call semantics.
/// </summary>
public static class CodeAtlasJavaIndexer
{
    private static readonly Regex Package = new(
        @"^package\s+([A-Za-z_$][A-Za-z_$0-9]*(?:\.[A-Za-z_$][A-Za-z_$0-9]*)*)\s*;",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TypeDeclaration = new(
        @"^(?:(?:public|protected|private|abstract|static|final|sealed|non-sealed|strictfp)\s+)*(class|interface|enum|record)\s+([A-Za-z_$][A-Za-z_$0-9]*)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NameBeforeParen = new(
        @"([A-Za-z_$][A-Za-z_$0-9]*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private sealed record TypeScope(string Name, string Qualified,
        string Id, int Depth);

    private static int Closing(string line, int start)
    {
        var depth = 0;
        for (var i = start; i < line.Length; i++)
        {
            if (line[i] == '(') depth++;
            else if (line[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    // Mask Java comments, character/string literals and text blocks; keep
    // structural punctuation and source-line positions intact.
    private static string Mask(string line, ref bool blockComment,
        ref bool textBlock, ref bool incomplete)
    {
        var chars = line.ToCharArray();
        void Blank(int from, int to)
        {
            for (var j = from; j < to; j++) chars[j] = ' ';
        }
        var i = 0;
        while (i < line.Length)
        {
            if (blockComment)
            {
                var from = i;
                var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                i = end < 0 ? line.Length : end + 2;
                Blank(from, i);
                if (end >= 0) blockComment = false;
                continue;
            }
            if (textBlock)
            {
                var from = i;
                var end = line.IndexOf("\"\"\"", i, StringComparison.Ordinal);
                i = end < 0 ? line.Length : end + 3;
                Blank(from, i);
                if (end >= 0) textBlock = false;
                continue;
            }
            if (line[i] == '/' && i + 1 < line.Length &&
                line[i + 1] == '/')
            {
                Blank(i, line.Length);
                break;
            }
            if (line[i] == '/' && i + 1 < line.Length &&
                line[i + 1] == '*')
            {
                blockComment = true;
                var from = i;
                i += 2;
                Blank(from, i);
                continue;
            }
            if (line[i] is not ('"' or '\''))
            {
                i++;
                continue;
            }
            var quote = line[i];
            var fromQuote = i;
            if (quote == '"' && i + 2 < line.Length &&
                line[i + 1] == '"' && line[i + 2] == '"')
            {
                textBlock = true;
                i += 3;
                Blank(fromQuote, i);
                continue;
            }
            i++;
            var closed = false;
            while (i < line.Length)
            {
                if (line[i] == '\\')
                {
                    i = Math.Min(i + 2, line.Length);
                    continue;
                }
                if (line[i] == quote)
                {
                    i++;
                    closed = true;
                    break;
                }
                i++;
            }
            if (!closed) incomplete = true;
            Blank(fromQuote, i);
        }
        return new string(chars);
    }

    public static AtlasSnapshot Enrich(AtlasSnapshot graph,
        IReadOnlyList<EngineeringSourceFile> source)
    {
        var indexedFiles = graph.Nodes.Where(n => n.Kind == "File" &&
            n.Path is not null).ToDictionary(n => n.Path!,
                StringComparer.Ordinal);
        var candidates = source.Where(f =>
            indexedFiles.ContainsKey(f.Path) &&
            f.Path.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0) return graph;

        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var edges = graph.Edges.ToDictionary(e =>
            e.Kind + "|" + e.SourceId + "|" + e.TargetId,
            StringComparer.Ordinal);
        var diagnostics = graph.Diagnostics.ToList();
        var partial = false;
        var root = graph.Nodes.FirstOrDefault(n => n.Kind == "Repository")
            ?? throw new StoreFailure("ATLAS_SNAPSHOT_CORRUPT");

        void Problem(string reason, string path)
        {
            partial = true;
            if (diagnostics.Count < 80)
                diagnostics.Add("JAVA_" + reason + ":" + path);
        }
        string Add(string kind, string name, string qualified,
            string parent, string path, int line)
        {
            var id = CodeAtlasIndexer.Id(kind, qualified);
            if (nodes.ContainsKey(id)) return id;
            if (nodes.Count >= CodeAtlasIndexer.MaximumNodes)
            {
                Problem("NODE_LIMIT", path);
                return "";
            }
            nodes.Add(id, new AtlasNode(id, kind, name, qualified,
                parent, path, line));
            return id;
        }
        void Link(string from, string to, string kind, string path)
        {
            if (from.Length == 0 || to.Length == 0) return;
            var key = kind + "|" + from + "|" + to;
            if (edges.ContainsKey(key)) return;
            if (edges.Count >= CodeAtlasIndexer.MaximumEdges)
            {
                Problem("EDGE_LIMIT", path);
                return;
            }
            edges.Add(key, new AtlasEdge(from, to, kind));
        }

        foreach (var file in candidates)
        {
            var fileId = indexedFiles[file.Path].Id;
            var lines = file.Content.Split('\n');
            var maxLines = Math.Min(lines.Length, 12000);
            if (maxLines < lines.Length) Problem("LINE_LIMIT", file.Path);
            var blockComment = false;
            var textBlock = false;
            var depth = 0;
            var packageName = "";
            var namespaceId = "";
            var types = new Stack<TypeScope>();

            for (var n = 0; n < maxLines; n++)
            {
                var literalError = false;
                var line = Mask(lines[n].TrimEnd('\r'),
                    ref blockComment, ref textBlock, ref literalError);
                if (literalError) Problem("UNCLOSED_LITERAL", file.Path);
                var code = line.Trim();
                if (code.Length == 0) continue;
                while (types.Count > 0 && depth < types.Peek().Depth)
                    types.Pop();

                if (depth == 0)
                {
                    var pkg = Package.Match(code);
                    if (pkg.Success)
                    {
                        packageName = pkg.Groups[1].Value;
                        namespaceId = Add("Namespace", packageName,
                            graph.RunId + "|java-package:" + packageName,
                            root.Id, file.Path, n + 1);
                        Link(root.Id, namespaceId, "Contains", file.Path);
                        Link(fileId, namespaceId, "Declares", file.Path);
                    }
                }

                var owningType = types.Count == 0 ? null : types.Peek();
                var declarationPosition = owningType is null ?
                    depth == 0 : depth == owningType.Depth;
                var foundType = false;
                if (declarationPosition)
                {
                    var match = TypeDeclaration.Match(code);
                    if (match.Success)
                    {
                        var open = code.IndexOf('{', match.Length);
                        if (open < 0)
                        {
                            Problem("UNSUPPORTED_TYPE", file.Path);
                        }
                        else
                        {
                            var kind = match.Groups[1].Value switch
                            {
                                "interface" => "Interface",
                                "enum" => "Enum",
                                _ => "Class"
                            };
                            var name = match.Groups[2].Value;
                            var relative = owningType is null ? name :
                                owningType.Qualified + "." + name;
                            var fq = (packageName.Length == 0 ? "" :
                                packageName + ".") + relative;
                            var id = Add(kind, name,
                                graph.RunId + "|" + file.Path + "|" + fq,
                                owningType?.Id ?? (namespaceId.Length == 0 ?
                                    fileId : namespaceId),
                                file.Path, n + 1);
                            Link(owningType?.Id ?? (namespaceId.Length == 0 ?
                                fileId : namespaceId),
                                id, "Contains", file.Path);
                            Link(fileId, id, "Declares", file.Path);
                            if (id.Length > 0)
                                types.Push(new TypeScope(name, relative, id,
                                    depth + 1));
                            foundType = true;
                            var inline = code[(open + 1)..].Trim();
                            if (inline.Length > 0 && inline != "}")
                                Problem("INLINE_BODY_PARTIAL", file.Path);
                        }
                    }
                }

                if (!foundType && owningType is not null &&
                    depth == owningType.Depth &&
                    !code.Contains('=') &&
                    !code.Contains("->", StringComparison.Ordinal))
                {
                    var open = code.IndexOf('(');
                    if (open > 0)
                    {
                        var close = Closing(code, open);
                        if (close < 0)
                        {
                            Problem("UNSUPPORTED_SIGNATURE", file.Path);
                        }
                        else
                        {
                            var prefix = code[..open].TrimEnd();
                            var nameMatch = NameBeforeParen.Match(prefix);
                            if (nameMatch.Success)
                            {
                                var name = nameMatch.Groups[1].Value;
                                var modifiersOrType = prefix[..nameMatch.Index]
                                    .Trim();
                                var tail = code[(close + 1)..].TrimStart();
                                var validBody = tail.StartsWith('{') ||
                                    tail.StartsWith(';') ||
                                    (tail.StartsWith("throws ", StringComparison.Ordinal) &&
                                     (tail.Contains('{') || tail.Contains(';')));
                                var constructor = name == owningType.Name;
                                var callable = !modifiersOrType.EndsWith('.') &&
                                    modifiersOrType != "return" &&
                                    modifiersOrType != "new" &&
                                    modifiersOrType != "throw" &&
                                    (constructor || modifiersOrType.Length > 0);
                                if (validBody && callable &&
                                    name is not ("if" or "while" or "for" or
                                        "switch" or "catch" or "super" or "this"))
                                {
                                    var args = code[(open + 1)..close]
                                        .Replace(" ", "", StringComparison.Ordinal);
                                    if (args.Length > 128)
                                        args = args[..128] + "…";
                                    var kind = constructor ?
                                        "Constructor" : "Method";
                                    var label = name + "(" + args + ")";
                                    var qualified = graph.RunId + "|" +
                                        file.Path + "|" + owningType.Qualified +
                                        "." + label;
                                    var id = Add(kind, label, qualified,
                                        owningType.Id, file.Path, n + 1);
                                    Link(owningType.Id, id, "Contains", file.Path);
                                    Link(fileId, id, "Declares", file.Path);
                                }
                            }
                        }
                    }
                }

                depth += code.Count(c => c == '{') -
                    code.Count(c => c == '}');
                if (depth < 0)
                {
                    Problem("UNBALANCED_BRACES", file.Path);
                    depth = 0;
                    types.Clear();
                }
                while (types.Count > 0 && depth < types.Peek().Depth)
                    types.Pop();
            }
            if (depth != 0) Problem("UNBALANCED_BRACES", file.Path);
            if (blockComment) Problem("UNCLOSED_COMMENT", file.Path);
            if (textBlock) Problem("UNCLOSED_TEXT_BLOCK", file.Path);
        }
        var otherLanguages = source.Any(f =>
            indexedFiles.ContainsKey(f.Path) &&
            !f.Path.EndsWith(".java", StringComparison.OrdinalIgnoreCase) &&
            (f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".py", StringComparison.OrdinalIgnoreCase)));
        var coverage = partial ? "PARTIAL_JAVA_STRUCTURE" :
            "JAVA_STRUCTURE_ONLY";
        if (otherLanguages) coverage = graph.Coverage + "+" + coverage;
        return graph with
        {
            Coverage = coverage,
            Nodes = nodes.Values.OrderBy(n => n.Kind, StringComparer.Ordinal)
                .ThenBy(n => n.QualifiedName, StringComparer.Ordinal).ToArray(),
            Edges = edges.Values.OrderBy(e => e.Kind, StringComparer.Ordinal)
                .ThenBy(e => e.SourceId, StringComparer.Ordinal)
                .ThenBy(e => e.TargetId, StringComparer.Ordinal).ToArray(),
            Diagnostics = diagnostics.AsReadOnly()
        };
    }
}
