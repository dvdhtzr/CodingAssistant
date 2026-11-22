using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

/// <summary>
/// M8.6. Read-only, bounded Python declaration structure. This deliberately
/// does not invoke Python, load imports, run packages or infer call semantics.
/// All information derives from accepted source text already in the graph.
/// </summary>
public static class CodeAtlasPythonIndexer
{
    private static readonly Regex Declaration = new(
        @"^(?:(?:async)[ ]+)?(class|def)[ ]+([A-Za-z_][A-Za-z_0-9]*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed record Scope(int Indent, string Qualified, string Id,
        string Kind);

    // Replace strings and comments with spaces while preserving column
    // positions. Triple-quoted strings may span lines. No Python execution.
    private static string Mask(string line, ref char triple, ref bool incomplete)
    {
        var output = line.ToCharArray();
        void Blank(int from, int to)
        {
            for (var at = from; at < to; at++) output[at] = ' ';
        }
        var i = 0;
        while (i < line.Length)
        {
            if (triple != '\0')
            {
                var start = i;
                while (i < line.Length)
                {
                    if (line[i] == '\\')
                    {
                        i = Math.Min(i + 2, line.Length);
                        continue;
                    }
                    if (i + 2 < line.Length &&
                        line[i] == triple && line[i + 1] == triple &&
                        line[i + 2] == triple)
                    {
                        i += 3;
                        triple = '\0';
                        break;
                    }
                    i++;
                }
                Blank(start, i);
                continue;
            }
            if (line[i] == '#')
            {
                Blank(i, line.Length);
                break;
            }
            if (line[i] is not ('\'' or '"'))
            {
                i++;
                continue;
            }
            var quote = line[i];
            var begin = i;
            if (i + 2 < line.Length &&
                line[i + 1] == quote && line[i + 2] == quote)
            {
                triple = quote;
                i += 3;
                Blank(begin, i);
                continue;
            }
            i++;
            var terminated = false;
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
                    terminated = true;
                    break;
                }
                i++;
            }
            if (!terminated) incomplete = true;
            Blank(begin, i);
        }
        return new string(output);
    }

    private static int ClosingParen(string source, int opening)
    {
        var count = 0;
        for (var i = opening; i < source.Length; i++)
        {
            if (source[i] == '(') count++;
            else if (source[i] == ')' && --count == 0) return i;
        }
        return -1;
    }

    private static bool ValidDefinition(string masked, int afterName,
        string kind, out int signatureClose)
    {
        signatureClose = -1;
        var tail = masked[afterName..].TrimStart();
        if (kind == "class")
        {
            if (tail.StartsWith('('))
            {
                var close = ClosingParen(tail, 0);
                if (close < 0) return false;
                tail = tail[(close + 1)..].TrimStart();
            }
            return tail.StartsWith(':');
        }
        if (!tail.StartsWith('(')) return false;
        var closing = ClosingParen(tail, 0);
        if (closing < 0) return false;
        signatureClose = closing;
        return tail[(closing + 1)..].Contains(':');
    }

    public static AtlasSnapshot Enrich(AtlasSnapshot graph,
        IReadOnlyList<EngineeringSourceFile> source)
    {
        var files = graph.Nodes.Where(n =>
            n.Kind == "File" && n.Path is not null)
            .ToDictionary(n => n.Path!, StringComparer.Ordinal);
        var python = source.Where(f =>
            files.ContainsKey(f.Path) &&
            f.Path.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        if (python.Length == 0) return graph;

        var nodes = graph.Nodes.ToDictionary(n => n.Id,
            StringComparer.Ordinal);
        var edges = graph.Edges.ToDictionary(e =>
            e.Kind + "|" + e.SourceId + "|" + e.TargetId,
            StringComparer.Ordinal);
        var diagnostics = graph.Diagnostics.ToList();
        var partial = false;

        void Diagnostic(string code, string path)
        {
            partial = true;
            if (diagnostics.Count < 80)
                diagnostics.Add("PYTHON_" + code + ":" + path);
        }

        string Add(string kind, string label, string qualified,
            string parent, string path, int line)
        {
            var id = CodeAtlasIndexer.Id(kind, qualified);
            if (nodes.ContainsKey(id)) return id;
            if (nodes.Count >= CodeAtlasIndexer.MaximumNodes)
            {
                Diagnostic("NODE_LIMIT", path);
                return "";
            }
            nodes.Add(id, new AtlasNode(id, kind, label, qualified,
                parent, path, line));
            return id;
        }

        void Link(string from, string to, string kind,
            string path)
        {
            if (from.Length == 0 || to.Length == 0) return;
            var key = kind + "|" + from + "|" + to;
            if (edges.ContainsKey(key)) return;
            if (edges.Count >= CodeAtlasIndexer.MaximumEdges)
            {
                Diagnostic("EDGE_LIMIT", path);
                return;
            }
            edges.Add(key, new AtlasEdge(from, to, kind));
        }

        foreach (var file in python)
        {
            var fileId = files[file.Path].Id;
            var scopes = new Stack<Scope>();
            var lines = file.Content.Split('\n');
            var triple = '\0';
            var multiline = false;
            var maxLines = Math.Min(lines.Length, 12000);
            if (maxLines < lines.Length)
                Diagnostic("LINE_LIMIT", file.Path);
            for (var n = 0; n < maxLines; n++)
            {
                var raw = lines[n].TrimEnd('\r');
                var badLiteral = false;
                var masked = Mask(raw, ref triple, ref badLiteral);
                if (badLiteral) Diagnostic("UNCLOSED_LITERAL", file.Path);
                if (string.IsNullOrWhiteSpace(masked)) continue;
                var lead = 0;
                while (lead < masked.Length &&
                    (masked[lead] == ' ' || masked[lead] == '\t'))
                    lead++;
                if (masked[..lead].Contains('\t'))
                {
                    Diagnostic("TAB_INDENT", file.Path);
                    continue;
                }
                var content = masked[lead..];
                // Multiline signatures/expressions cannot be reliably
                // indexed without a complete Python grammar. Report partial
                // coverage rather than inventing a declaration.
                if (multiline)
                {
                    if (content.EndsWith(':') || content.EndsWith(')'))
                        multiline = false;
                    continue;
                }
                while (scopes.Count > 0 && lead <= scopes.Peek().Indent)
                    scopes.Pop();

                var match = Declaration.Match(content);
                if (!match.Success) continue;
                var kindToken = match.Groups[1].Value;
                var name = match.Groups[2].Value;
                if (!ValidDefinition(content, match.Length,
                    kindToken, out _))
                {
                    Diagnostic("UNSUPPORTED_DEFINITION", file.Path);
                    if (content.Contains('(') &&
                        !content.Contains(')')) multiline = true;
                    continue;
                }
                var owner = scopes.Count == 0 ? null : scopes.Peek();
                var isMethod = kindToken == "def" &&
                    owner?.Kind == "Class";
                var kind = kindToken == "class" ? "Class" :
                    isMethod && name == "__init__" ? "Constructor" :
                    isMethod ? "Method" : "Function";
                var qualified = graph.RunId + "|" + file.Path + "|" +
                    (owner is null ? name : owner.Qualified + "." + name);
                var parent = owner?.Id ?? fileId;
                var id = Add(kind, name, qualified,
                    parent, file.Path, n + 1);
                Link(parent, id, "Contains", file.Path);
                if (parent != fileId) Link(fileId, id, "Declares", file.Path);
                if (id.Length != 0)
                    scopes.Push(new Scope(lead,
                        owner is null ? name : owner.Qualified + "." + name,
                        id, kind));
            }
            if (triple != '\0') Diagnostic("UNCLOSED_TRIPLE_QUOTE", file.Path);
        }
        var otherLanguages = source.Any(f =>
            files.ContainsKey(f.Path) && !f.Path.EndsWith(".py",
                StringComparison.OrdinalIgnoreCase) &&
            (f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase)));
        var coverage = partial ? "PARTIAL_PYTHON_STRUCTURE" :
            "PYTHON_STRUCTURE_ONLY";
        if (otherLanguages)
            coverage = graph.Coverage + "+" + coverage;
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
