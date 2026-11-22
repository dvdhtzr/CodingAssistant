namespace CodingAssistant.Api;

/// <summary>
/// M8.2: conservative TypeScript/JavaScript declaration indexing.
/// This is a bounded lexical source-structure projection, NOT a JS/TS
/// semantic compiler. No source, packages, scripts, imports or build steps
/// are executed, and no calls/inheritance are invented from syntax.
/// </summary>
public static class CodeAtlasEcmaIndexer
{
    private sealed record Token(string Text, int Line, int Depth);
    private const int MaximumTokensPerFile = 20000;
    private static readonly HashSet<string> Extensions = new(
        [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RegexPrefixes = new(
        ["=", "(", "[", "{", ":", ",", "return", "=>", "!", "?", ";"],
        StringComparer.Ordinal);
    private static bool WordStart(char c) => char.IsLetter(c) ||
        c is '_' or '$';
    private static bool WordPart(char c) => WordStart(c) || char.IsDigit(c);
    private static bool Word(string name) => name.Length > 0 &&
        WordStart(name[0]) && name.Skip(1).All(WordPart);

    // Source is input DATA. Comments, quoted text, templates and regexp
    // literals never become declarations, even if they contain keywords.
    private static List<Token> Lex(string source, string path,
        List<string> diagnostics, out bool incomplete)
    {
        var tokens = new List<Token>();
        incomplete = false;
        var line = 1;
        var depth = 0;
        var i = 0;
        void Partial(string reason)
        {
            incomplete = true;
            var code = "ECMA_" + reason + ":" + path;
            if (!diagnostics.Contains(code)) diagnostics.Add(code);
        }
        while (i < source.Length)
        {
            var ch = source[i];
            if (ch == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == '/' && i + 1 < source.Length &&
                source[i + 1] == '/')
            {
                i += 2;
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (ch == '/' && i + 1 < source.Length &&
                source[i + 1] == '*')
            {
                i += 2;
                var closed = false;
                while (i < source.Length)
                {
                    if (source[i] == '\n') line++;
                    if (source[i] == '*' && i + 1 < source.Length &&
                        source[i + 1] == '/')
                    { i += 2; closed = true; break; }
                    i++;
                }
                if (!closed) Partial("UNCLOSED_COMMENT");
                continue;
            }
            if (ch is '"' or '\'' || ch == (char)96)
            {
                var quote = ch;
                i++;
                var closed = false;
                var interpolation = false;
                while (i < source.Length)
                {
                    if (source[i] == '\\') { i = Math.Min(source.Length, i + 2); continue; }
                    if (source[i] == '\n') line++;
                    if (quote == (char)96 && source[i] == '$' &&
                        i + 1 < source.Length && source[i + 1] == '{')
                        interpolation = true;
                    if (source[i] == quote) { i++; closed = true; break; }
                    i++;
                }
                if (!closed) Partial("UNCLOSED_LITERAL");
                if (interpolation) Partial("TEMPLATE_EXPRESSIONS_NOT_INDEXED");
                continue;
            }
            if (ch == '/' && (tokens.Count == 0 ||
                RegexPrefixes.Contains(tokens[^1].Text)))
            {
                var at = i + 1;
                var inClass = false;
                var closed = false;
                while (at < source.Length && source[at] != '\n')
                {
                    if (source[at] == '\\') { at = Math.Min(source.Length, at + 2); continue; }
                    if (source[at] == '[') inClass = true;
                    else if (source[at] == ']') inClass = false;
                    else if (source[at] == '/' && !inClass)
                    { closed = true; break; }
                    at++;
                }
                if (closed)
                {
                    i = at + 1;
                    while (i < source.Length && char.IsLetter(source[i])) i++;
                    continue;
                }
            }
            var start = i;
            if (WordStart(ch))
            {
                i++;
                while (i < source.Length && WordPart(source[i])) i++;
            }
            else if (ch == '=' && i + 1 < source.Length &&
                source[i + 1] == '>')
                i += 2;
            else i++;
            var text = source[start..i];
            tokens.Add(new(text, line, depth));
            if (text == "{") depth++;
            else if (text == "}")
            {
                depth--;
                if (depth < 0) { Partial("UNBALANCED_BRACES"); depth = 0; }
            }
            if (tokens.Count >= MaximumTokensPerFile)
            {
                Partial("TOKEN_LIMIT");
                break;
            }
        }
        if (depth != 0) Partial("UNBALANCED_BRACES");
        return tokens;
    }

    private static int Closing(IReadOnlyList<Token> tokens, int opening,
        string open, string close, int limit)
    {
        if (opening < 0 || opening >= limit ||
            tokens[opening].Text != open) return -1;
        var nesting = 0;
        for (var i = opening; i < limit; i++)
        {
            if (tokens[i].Text == open) nesting++;
            else if (tokens[i].Text == close && --nesting == 0) return i;
        }
        return -1;
    }

    private static int ParametersAt(IReadOnlyList<Token> tokens, int index,
        int end)
    {
        if (index >= end) return -1;
        if (tokens[index].Text == "?") index++;
        if (index < end && tokens[index].Text == "<")
        {
            var close = Closing(tokens, index, "<", ">", end);
            if (close < 0) return -1;
            index = close + 1;
        }
        return index < end && tokens[index].Text == "(" ? index : -1;
    }

    private static string Signature(IReadOnlyList<Token> tokens,
        int open, int close)
    {
        var content = string.Concat(tokens.Skip(open + 1)
            .Take(close - open - 1).Select(t => t.Text));
        // Content is a textual signature only, never resolved type identity.
        return content.Length <= 160 ? content : content[..160] + "…";
    }

    public static AtlasSnapshot Enrich(AtlasSnapshot graph,
        IReadOnlyList<EngineeringSourceFile> source)
    {
        var files = graph.Nodes.Where(n => n.Kind == "File" &&
            n.Path is not null).ToDictionary(n => n.Path!, n => n,
                StringComparer.Ordinal);
        var candidates = source.Where(f => files.ContainsKey(f.Path) &&
            Extensions.Contains(Path.GetExtension(f.Path)))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0) return graph;

        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var edges = graph.Edges.ToDictionary(e =>
            e.Kind + "|" + e.SourceId + "|" + e.TargetId,
            StringComparer.Ordinal);
        var diagnostics = graph.Diagnostics.ToList();
        var incomplete = false;

        string Add(string kind, string identity, string label,
            string parent, string path, int line)
        {
            var id = CodeAtlasIndexer.Id(kind, identity);
            if (nodes.ContainsKey(id)) return id;
            if (nodes.Count >= CodeAtlasIndexer.MaximumNodes)
            {
                if (!diagnostics.Contains("ECMA_NODE_LIMIT"))
                    diagnostics.Add("ECMA_NODE_LIMIT");
                incomplete = true;
                return "";
            }
            var node = new AtlasNode(id, kind, label, identity,
                parent, path, line);
            nodes.Add(id, node);
            return id;
        }
        void Link(string from, string to, string kind)
        {
            if (!nodes.ContainsKey(from) || !nodes.ContainsKey(to)) return;
            var key = kind + "|" + from + "|" + to;
            if (edges.ContainsKey(key)) return;
            if (edges.Count >= CodeAtlasIndexer.MaximumEdges)
            {
                if (!diagnostics.Contains("ECMA_EDGE_LIMIT"))
                    diagnostics.Add("ECMA_EDGE_LIMIT");
                incomplete = true;
                return;
            }
            edges.Add(key, new AtlasEdge(from, to, kind));
        }

        foreach (var file in candidates)
        {
            var fileNode = files[file.Path];
            var tokens = Lex(file.Content, file.Path, diagnostics,
                out var lexIncomplete);
            incomplete |= lexIncomplete;
            var identity = graph.RunId + "|" + file.Path + "|";

            void Declare(string kind, string qualified, string name,
                string parentId, int line)
            {
                var node = Add(kind, identity + qualified, name,
                    parentId, file.Path, line);
                Link(parentId, node, "Contains");
                if (parentId != fileNode.Id) Link(fileNode.Id, node, "Declares");
            }

            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Depth != 0) continue;
                if (token.Text is "class" or "interface" &&
                    i + 1 < tokens.Count && Word(tokens[i + 1].Text))
                {
                    // A named class/interface and a matching body at top level.
                    var name = tokens[i + 1].Text;
                    var body = -1;
                    for (var j = i + 2; j < tokens.Count &&
                        j < i + 100; j++)
                    {
                        if (tokens[j].Depth != 0) break;
                        if (tokens[j].Text is ";" or "=") break;
                        if (tokens[j].Text == "{") { body = j; break; }
                    }
                    if (body < 0) continue;
                    var end = Closing(tokens, body, "{", "}", tokens.Count);
                    if (end < 0) { incomplete = true; continue; }
                    var kind = token.Text == "class" ? "Class" : "Interface";
                    var typeKey = name;
                    var typeId = Add(kind, identity + typeKey, name,
                        fileNode.Id, file.Path, token.Line);
                    Link(fileNode.Id, typeId, "Contains");

                    for (var j = body + 1; j < end; j++)
                    {
                        if (tokens[j].Depth != 1 ||
                            !Word(tokens[j].Text)) continue;
                        var previous = j > body + 1 ? tokens[j - 1].Text : "{";
                        if (previous is "=" or "." or ":" or "new" or
                            "return" or "=>" or "(" or ",") continue;
                        var param = ParametersAt(tokens, j + 1, end);
                        if (param < 0) continue;
                        var close = Closing(tokens, param, "(", ")", end);
                        if (close < 0) continue;
                        // Only a member declaration: a semicolon, a method
                        // body, or a TS return annotation after parameters.
                        if (close + 1 >= end ||
                            tokens[close + 1].Text is not ("{" or ":" or ";" or "?"))
                            continue;
                        var memberName = tokens[j].Text;
                        var constructor = memberName == "constructor";
                        var memberKind = constructor ? "Constructor" : "Method";
                        var label = memberName + "(" + Signature(tokens,
                            param, close) + ")";
                        var memberKey = name + "." + label;
                        var memberId = Add(memberKind, identity + memberKey,
                            label, typeId, file.Path, tokens[j].Line);
                        Link(typeId, memberId, "Contains");
                        Link(fileNode.Id, memberId, "Declares");
                    }
                    i = end;
                    continue;
                }
                if (token.Text == "function" && i + 1 < tokens.Count &&
                    Word(tokens[i + 1].Text))
                {
                    var name = tokens[i + 1].Text;
                    var open = ParametersAt(tokens, i + 2, tokens.Count);
                    if (open < 0) continue;
                    var close = Closing(tokens, open, "(", ")", tokens.Count);
                    if (close < 0) continue;
                    var label = name + "(" + Signature(tokens, open, close) + ")";
                    Declare("Function", label, label, fileNode.Id, token.Line);
                    i = close;
                    continue;
                }
                if (token.Text is not ("const" or "let" or "var") ||
                    i + 1 >= tokens.Count || !Word(tokens[i + 1].Text))
                    continue;
                var binding = tokens[i + 1].Text;
                var arrow = -1;
                for (var j = i + 2; j < tokens.Count && j < i + 120; j++)
                {
                    if (tokens[j].Depth != 0 || tokens[j].Text == ";") break;
                    if (tokens[j].Text == "=>") { arrow = j; break; }
                }
                if (arrow < 0) continue;
                // A top-level named arrow assignment is source structure,
                // not proof of React component behavior or any call edge.
                var signature = binding + "(arrow)";
                Declare("Function", signature, signature, fileNode.Id, token.Line);
                i = arrow;
            }
        }
        var hasCSharp = source.Any(f => files.ContainsKey(f.Path) &&
            f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        var coverage = hasCSharp ?
            (graph.Coverage == "STATIC_SEMANTIC" && !incomplete ?
                "MIXED_CSHARP_STATIC_ECMA_STRUCTURE" :
                "MIXED_PARTIAL_CSHARP_ECMA_STRUCTURE") :
            incomplete ? "PARTIAL_ECMA_STRUCTURE" : "ECMA_STRUCTURE_ONLY";
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
