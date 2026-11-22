namespace CodingAssistant.Api;

/// <summary>
/// M8.2: conservative TypeScript/JavaScript declaration indexing.
/// This is a bounded lexical source-structure projection, NOT a JS/TS
/// semantic compiler. No source, packages, scripts, imports or build steps
/// are executed, and no calls/inheritance are invented from syntax.
/// </summary>
public static class CodeAtlasEcmaIndexer
{
    private sealed record Token(string Text, int Line, int Depth,
        string? Literal = null);
    private const string LiteralToken = "#literal";
    private static readonly string[] LocalModuleExtensions =
        [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"];
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
        var partial = false;
        var line = 1;
        var depth = 0;
        var i = 0;
        void Partial(string reason)
        {
            partial = true;
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
                var literalLine = line;
                var begin = ++i;
                var closed = false;
                var escaped = false;
                var interpolation = false;
                while (i < source.Length)
                {
                    if (source[i] == '\\')
                    {
                        escaped = true;
                        i = Math.Min(source.Length, i + 2);
                        continue;
                    }
                    if (source[i] == '\n') line++;
                    if (quote == (char)96 && source[i] == '$' &&
                        i + 1 < source.Length && source[i + 1] == '{')
                        interpolation = true;
                    if (source[i] == quote) { i++; closed = true; break; }
                    i++;
                }
                if (!closed) Partial("UNCLOSED_LITERAL");
                if (interpolation) Partial("TEMPLATE_EXPRESSIONS_NOT_INDEXED");
                // Retain only small, unescaped ordinary strings as inert
                // tokens. A template/escape must never be resolved as a path.
                var length = closed ? i - begin - 1 : 0;
                var literal = closed && !escaped && quote != (char)96 &&
                    length is > 0 and <= 240
                    ? source.Substring(begin, length) : null;
                tokens.Add(new(LiteralToken, literalLine, depth, literal));
                if (tokens.Count >= MaximumTokensPerFile)
                {
                    Partial("TOKEN_LIMIT");
                    break;
                }
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
        incomplete = partial;
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

    /// <summary>
    /// Recognize only top-level ES import/export-from declarations and
    /// side-effect imports. Dynamic import(), require(), package names,
    /// macros, aliases and type resolution are intentionally not inferred.
    /// </summary>
    private static IReadOnlyList<(string Literal, int Line)> Imports(
        IReadOnlyList<Token> tokens)
    {
        var found = new List<(string Literal, int Line)>();
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var marker = tokens[i];
            if (marker.Depth != 0 ||
                marker.Text is not ("import" or "export") ||
                (i > 0 && tokens[i - 1].Text == "."))
                continue;
            var next = tokens[i + 1].Text;
            if (marker.Text == "import" && next == LiteralToken)
            {
                if (tokens[i + 1].Literal is { } sideEffect)
                    found.Add((sideEffect, marker.Line));
                continue;
            }
            if (marker.Text == "import" && next is "(" or ".")
                continue;
            if (marker.Text == "export" && next is not ("{" or "*" or "type"))
                continue;
            for (var j = i + 1; j + 1 < tokens.Count &&
                j < i + 96; j++)
            {
                if (tokens[j].Depth == 0 &&
                    tokens[j].Text is (";" or "import" or "export"))
                    break;
                if (tokens[j].Depth == 0 && tokens[j].Text == "from" &&
                    tokens[j + 1].Text == LiteralToken)
                {
                    if (tokens[j + 1].Literal is { } name)
                        found.Add((name, marker.Line));
                    break;
                }
            }
        }
        return found;
    }

    /// <summary>
    /// Canonicalize relative specifiers lexically, without reading disk.
    /// Neither a path escape nor an ambiguous extension guess may create
    /// a graph link. The only targets are already indexed source files.
    /// </summary>
    private static (string? Target, string? Problem) Resolve(
        string path, string literal,
        IReadOnlyDictionary<string, AtlasNode> indexedFiles)
    {
        if (!(literal.StartsWith("./", StringComparison.Ordinal) ||
              literal.StartsWith("../", StringComparison.Ordinal)) ||
            literal.Length > 200 || literal.Contains('\\') ||
            literal.Any(c => !char.IsAsciiLetterOrDigit(c) &&
                c is not ('/' or '.' or '_' or '-')))
            return (null, null);

        var parts = path.Split('/').SkipLast(1).ToList();
        foreach (var component in literal.Split('/'))
        {
            if (component is "" or ".") continue;
            if (component == "..")
            {
                if (parts.Count == 0) return (null, "OUTSIDE_ROOT");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(component);
        }
        if (parts.Count == 0) return (null, "UNRESOLVED");
        var target = string.Join("/", parts);
        var options = new List<string> { target };
        if (Path.GetExtension(target).Length == 0)
        {
            options.AddRange(LocalModuleExtensions.Select(ext => target + ext));
            options.AddRange(LocalModuleExtensions.Select(ext =>
                target + "/index" + ext));
        }
        var matched = options.Where(indexedFiles.ContainsKey)
            .Distinct(StringComparer.Ordinal).ToArray();
        return matched.Length switch
        {
            1 => (matched[0], null),
            0 => (null, "UNRESOLVED"),
            _ => (null, "AMBIGUOUS")
        };
    }


    private sealed record NamedImport(string Module, string Name,
        string LocalName, int Line, bool TypeOnly);

    /// <summary>
    /// Only simple, top-level ES named imports with an ordinary literal
    /// relative module. No default, namespace, dynamic, re-export, alias
    /// resolution, type checking, package lookup, or executable JS.
    /// </summary>
    private static IReadOnlyList<NamedImport> NamedImports(
        IReadOnlyList<Token> tokens)
    {
        var found = new List<NamedImport>();
        for (var i = 0; i + 3 < tokens.Count; i++)
        {
            if (tokens[i].Depth != 0 || tokens[i].Text != "import" ||
                (i > 0 && tokens[i - 1].Text == ".")) continue;
            var start = i + 1;
            var wholeTypeOnly = tokens[start].Text == "type";
            if (wholeTypeOnly) start++;
            if (start >= tokens.Count || tokens[start].Text != "{") continue;
            var end = Closing(tokens, start, "{", "}", tokens.Count);
            if (end < 0 || end + 2 >= tokens.Count ||
                tokens[end + 1].Text != "from" ||
                tokens[end + 2].Text != LiteralToken ||
                tokens[end + 2].Literal is not { } module) continue;
            var names = new List<(string Imported, string Local,
                bool TypeOnly)>();
            var valid = true;
            var j = start + 1;
            while (j < end)
            {
                if (names.Count == 32) { valid = false; break; }
                var oneTypeOnly = wholeTypeOnly ||
                    tokens[j].Text == "type";
                if (tokens[j].Text == "type") j++;
                if (j >= end || !Word(tokens[j].Text) ||
                    tokens[j].Text is "as" or "default")
                { valid = false; break; }
                var importedName = tokens[j++].Text;
                var localName = importedName;
                if (j < end && tokens[j].Text == "as")
                {
                    j++;
                    if (j >= end || !Word(tokens[j].Text))
                    { valid = false; break; }
                    localName = tokens[j++].Text;
                }
                names.Add((importedName, localName, oneTypeOnly));
                if (j == end) break;
                if (tokens[j].Text != ",") { valid = false; break; }
                j++;
            }
            if (valid)
                found.AddRange(names.Select(name =>
                    new NamedImport(module, name.Imported, name.Local,
                        tokens[i].Line, name.TypeOnly)));
            i = end + 2;
        }
        return found;
    }

    private static IReadOnlyList<(string Name, string Kind)> DirectExports(
        IReadOnlyList<Token> tokens)
    {
        var exports = new List<(string Name, string Kind)>();
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            if (tokens[i].Depth != 0 || tokens[i].Text != "export")
                continue;
            var j = i + 1;
            if (tokens[j].Text is "declare" or "async") j++;
            if (j + 1 >= tokens.Count || !Word(tokens[j + 1].Text))
                continue;
            var kind = tokens[j].Text switch
            {
                "class" => "Class",
                "interface" => "Interface",
                "function" or "const" => "Function",
                _ => ""
            };
            if (kind.Length > 0)
                exports.Add((tokens[j + 1].Text, kind));
        }
        return exports;
    }


    private sealed record TypeClause(string Owner, string OwnerKind,
        string Target, string Relation, int Line);

    // Only declarations at file scope. A class expression assigned to
    // a const must not be treated as a declaration for type binding.
    private static bool TypeDeclarationAt(IReadOnlyList<Token> t, int i)
    {
        if (i + 1 >= t.Count || t[i].Depth != 0 ||
            t[i].Text is not ("class" or "interface") ||
            !Word(t[i + 1].Text)) return false;
        if (i == 0) return true;
        return t[i - 1].Text is ";" or "}" or "export" or
            "default" or "declare" or "abstract";
    }

    // Parse a narrow type grammar: an unqualified name in an extends or
    // implements clause, optional comma-separated interfaces. Reject all
    // generic, computed, qualified, mixed-in and unrecognized syntax.
    private static IReadOnlyList<TypeClause> TypeClauses(
        IReadOnlyList<Token> t)
    {
        var found = new List<TypeClause>();
        for (var i = 0; i + 2 < t.Count; i++)
        {
            if (!TypeDeclarationAt(t, i)) continue;
            var end = -1;
            for (var j = i + 2; j < t.Count && j < i + 120; j++)
            {
                if (t[j].Depth != 0 || t[j].Text is ";" or "=") break;
                if (t[j].Text == "{") { end = j; break; }
            }
            if (end < 0) continue;
            var kind = t[i].Text;
            var name = t[i + 1].Text;
            var cursor = i + 2;
            var pending = new List<TypeClause>();
            var valid = true;

            bool One(string relationship)
            {
                if (cursor >= end || !Word(t[cursor].Text) ||
                    t[cursor].Text is "new" or "this" or "super")
                    return false;
                pending.Add(new TypeClause(name, kind, t[cursor++].Text,
                    relationship, t[i].Line));
                return true;
            }

            if (cursor < end && t[cursor].Text == "extends")
            {
                cursor++;
                if (kind == "class")
                    valid = One("Inherits");
                else
                {
                    valid = One("Inherits");
                    while (valid && cursor < end && t[cursor].Text == ",")
                    {
                        cursor++;
                        valid = One("Inherits");
                    }
                }
            }
            if (valid && kind == "class" && cursor < end &&
                t[cursor].Text == "implements")
            {
                cursor++;
                valid = One("Implements");
                while (valid && cursor < end && t[cursor].Text == ",")
                {
                    cursor++;
                    valid = One("Implements");
                }
            }
            if (valid && cursor == end)
                found.AddRange(pending);
        }
        return found;
    }


    private sealed record DirectFunction(string Name, int BodyOpen,
        int BodyClose, bool HasParameters);

    // M8.13: Only named file-scoped declarations with ordinary brace bodies.
    // No arrow/callback, function expression, overload or inferred scope.
    private static IReadOnlyList<DirectFunction> DirectFunctions(
        IReadOnlyList<Token> t)
    {
        var found = new List<DirectFunction>();
        for (var i = 0; i + 3 < t.Count; i++)
        {
            if (t[i].Depth != 0 || t[i].Text != "function" ||
                !Word(t[i + 1].Text)) continue;
            if (i > 0 && t[i - 1].Text is not
                (";" or "}" or "export" or "default" or "async"))
                continue;
            if (i >= 2 && t[i - 1].Text == "async" &&
                t[i - 2].Text is not
                    (";" or "}" or "export" or "default"))
                continue;
            var open = ParametersAt(t, i + 2, t.Count);
            if (open < 0) continue;
            var close = Closing(t, open, "(", ")", t.Count);
            if (close < 0 || close + 1 >= t.Count ||
                t[close + 1].Text != "{") continue;
            var bodyOpen = close + 1;
            var end = Closing(t, bodyOpen, "{", "}", t.Count);
            if (end < 0) continue;
            found.Add(new DirectFunction(t[i + 1].Text, bodyOpen,
                end, close != open + 1));
        }
        return found;
    }

    // The caller's lexical region may shadow or rebind any callee. Reject
    // those names for the whole caller, irrespective of declaration order.
    // Unresolved destructuring, eval, or with rejects the whole caller.
    private static (bool Unsafe, HashSet<string> Blocked) CallShadows(
        IReadOnlyList<Token> t, DirectFunction caller)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var unsafeScope = false;
        for (var i = caller.BodyOpen + 1; i < caller.BodyClose; i++)
        {
            var word = t[i].Text;
            var next = i + 1 < caller.BodyClose ? t[i + 1].Text : "";
            if (word is "eval" && next == "(" || word == "with")
                unsafeScope = true;
            if (word is "const" or "let" or "var" &&
                next is "{" or "[")
                unsafeScope = true;
            if (word is "const" or "let" or "var" or "function" or
                "class" or "interface" or "catch" &&
                Word(next)) names.Add(next);
            // Any assignment or update to an otherwise-resolved name may
            // change call behavior. Conservative false negatives are safer.
            if (Word(word) && next is "=" or "+" or "-")
                names.Add(word);
        }
        return (unsafeScope, names);
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
        var tokenCache = new Dictionary<string, List<Token>>(
            StringComparer.Ordinal);

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
        void Link(string from, string to, string kind,
            string? evidencePath = null, int? evidenceLine = null,
            string resolution = "Syntax")
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
            edges.Add(key, new AtlasEdge(from, to, kind,
                evidencePath, evidenceLine, resolution));
        }

        foreach (var file in candidates)
        {
            var fileNode = files[file.Path];
            var tokens = Lex(file.Content, file.Path, diagnostics,
                out var lexIncomplete);
            tokenCache[file.Path] = tokens;
            incomplete |= lexIncomplete;
            var identity = graph.RunId + "|" + file.Path + "|";
            foreach (var (literal, line) in Imports(tokens))
            {
                var (target, problem) = Resolve(file.Path, literal, files);
                if (target is not null)
                {
                    Link(fileNode.Id, files[target].Id, "ModuleImports",
                        file.Path, line, "LiteralLocalModule");
                }
                else if (problem is not null && diagnostics.Count < 64)
                {
                    diagnostics.Add("ECMA_LOCAL_IMPORT_" + problem +
                        ":" + file.Path + ":" + line);
                }
            }

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

        // M8.11: A named import can target only a DIRECT, uniquely indexed
        // export in the same saved graph. This is a source-level symbol
        // reference, not a call edge or compiler-backed semantic binding.
        var exported = new Dictionary<string,
            Dictionary<string, AtlasNode>>(StringComparer.Ordinal);
        foreach (var file in candidates)
        {
            var exports = new Dictionary<string, AtlasNode>(
                StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, kind) in DirectExports(tokenCache[file.Path]))
            {
                var matches = nodes.Values.Where(n => n.Path == file.Path &&
                    n.Kind == kind &&
                    (kind == "Function"
                        ? n.Name.StartsWith(name + "(", StringComparison.Ordinal)
                        : n.Name == name)).ToArray();
                // Constants without an indexed callable declaration are
                // unsupported symbol kinds, not ambiguous source. Once a
                // name is ambiguous it cannot become resolvable again.
                if (matches.Length == 0 || ambiguous.Contains(name))
                    continue;
                if (matches.Length != 1 || !exports.TryAdd(name, matches[0]))
                {
                    ambiguous.Add(name);
                    exports.Remove(name);
                }
            }
            foreach (var name in ambiguous)
            {
                incomplete = true;
                if (diagnostics.Count < 64)
                    diagnostics.Add("ECMA_EXPORT_AMBIGUOUS:" + file.Path +
                        ":" + name);
            }
            exported[file.Path] = exports;
        }

        foreach (var file in candidates)
        {
            foreach (var imported in NamedImports(tokenCache[file.Path]))
            {
                var (target, _) = Resolve(file.Path, imported.Module, files);
                if (target is null || !exported.TryGetValue(target,
                    out var declarations) ||
                    !declarations.TryGetValue(imported.Name, out var symbol))
                    continue;
                Link(files[file.Path].Id, symbol.Id, "SymbolImports",
                    file.Path, imported.Line, "ExplicitLocalNamedExport");
            }
        }

        // M8.12: file-scoped bare-name extends/implements relationships.
        // Bind only uniquely declared types or uniquely named local imports
        // with direct exports. Never infer a call or runtime implementation.
        foreach (var file in candidates)
        {
            var tokens = tokenCache[file.Path];
            var typeClauses = TypeClauses(tokens);
            if (typeClauses.Count == 0) continue;
            var ownTypes = tokens.Select((token, index) => (token, index))
                .Where(x => TypeDeclarationAt(tokens, x.index))
                .GroupBy(x => x.token.Text + "|" + tokens[x.index + 1].Text,
                    StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(),
                    StringComparer.Ordinal);
            var localBindings = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < tokens.Count; i++)
                if (tokens[i].Depth == 0 &&
                    (tokens[i].Text is "class" or "interface" or
                        "function" or "const" or "let" or "var") &&
                    Word(tokens[i + 1].Text))
                    localBindings.Add(tokens[i + 1].Text);

            var bindings = NamedImports(tokens);
            var uniqueImports = bindings.GroupBy(b => b.LocalName,
                    StringComparer.Ordinal)
                .Where(g => g.Count() == 1 &&
                    !localBindings.Contains(g.Key))
                .ToDictionary(g => g.Key, g => g.Single(),
                    StringComparer.Ordinal);

            AtlasNode? ResolveType(string name, string kind)
            {
                // Local declarations shadow imported bindings; duplicates
                // and incompatible kinds cannot be safely linked.
                if (localBindings.Contains(name))
                {
                    var declarationKind = kind == "Class" ?
                        "class" : "interface";
                    if (!ownTypes.TryGetValue(
                        declarationKind + "|" + name, out var count)
                        || count != 1 ||
                        bindings.Any(b => b.LocalName == name))
                        return null;
                    return nodes.Values.SingleOrDefault(n =>
                        n.Path == file.Path && n.Kind == kind &&
                        n.Name == name && n.ParentId == files[file.Path].Id);
                }
                if (!uniqueImports.TryGetValue(name, out var imported))
                    return null;
                var (target, _) = Resolve(file.Path, imported.Module, files);
                return target is not null &&
                    exported.TryGetValue(target, out var symbols) &&
                    symbols.TryGetValue(imported.Name, out var symbol) &&
                    symbol.Kind == kind ? symbol : null;
            }

            foreach (var clause in typeClauses)
            {
                if (!ownTypes.TryGetValue(
                    clause.OwnerKind + "|" + clause.Owner,
                    out var ownerCount) || ownerCount != 1)
                    continue;
                var owner = nodes.Values.SingleOrDefault(n =>
                    n.Path == file.Path && n.Name == clause.Owner &&
                    n.Kind == (clause.OwnerKind == "class"
                        ? "Class" : "Interface") &&
                    n.ParentId == files[file.Path].Id);
                if (owner is null) continue;
                var expectedKind = clause.Relation == "Implements" ||
                    clause.OwnerKind == "interface"
                    ? "Interface" : "Class";
                var target = ResolveType(clause.Target, expectedKind);
                if (target is null || owner.Id == target.Id) continue;
                Link(owner.Id, target.Id, clause.Relation,
                    file.Path, clause.Line, "ExplicitEcmaTypeClause");
            }
        }

        // M8.13: direct, unqualified call expressions at statement
        // boundaries in zero-argument, top-level named function bodies.
        // Targets must be uniquely declared same-file functions or named,
        // value-imported, directly exported functions from a saved file.
        // Evidence is lexical source syntax, never runtime call proof.
        var direct = candidates.ToDictionary(f => f.Path,
            f => DirectFunctions(tokenCache[f.Path]), StringComparer.Ordinal);
        var localNames = candidates.ToDictionary(f => f.Path, f =>
        {
            var t = tokenCache[f.Path];
            var names = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < t.Count; i++)
            {
                if (t[i].Depth != 0 ||
                    t[i].Text is not ("function" or "class" or
                        "interface" or "const" or "let" or "var") ||
                    !Word(t[i + 1].Text)) continue;
                names[t[i + 1].Text] =
                    names.GetValueOrDefault(t[i + 1].Text) + 1;
            }
            return names;
        }, StringComparer.Ordinal);

        AtlasNode? FunctionNode(string path, string name)
        {
            var matches = nodes.Values.Where(n =>
                n.Kind == "Function" && n.Path == path &&
                n.ParentId == files[path].Id &&
                n.Name.StartsWith(name + "(", StringComparison.Ordinal))
                .Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        foreach (var file in candidates)
        {
            var t = tokenCache[file.Path];
            var imported = NamedImports(t);
            var importsByName = imported
                .GroupBy(x => x.LocalName, StringComparer.Ordinal)
                .Where(g => g.Count() == 1 &&
                    !localNames[file.Path].ContainsKey(g.Key))
                .ToDictionary(g => g.Key, g => g.Single(),
                    StringComparer.Ordinal);
            var localDirect = direct[file.Path]
                .GroupBy(f => f.Name, StringComparer.Ordinal)
                .Where(g => g.Count() == 1 &&
                    localNames[file.Path].GetValueOrDefault(g.Key) == 1 &&
                    !imported.Any(x => x.LocalName == g.Key))
                .ToDictionary(g => g.Key, g => g.Single(),
                    StringComparer.Ordinal);
            foreach (var caller in direct[file.Path].Where(f =>
                !f.HasParameters && localDirect.ContainsKey(f.Name)))
            {
                var callerNode = FunctionNode(file.Path, caller.Name);
                if (callerNode is null) continue;
                var (unsafeScope, blocked) = CallShadows(t, caller);
                if (unsafeScope) continue;

                for (var i = caller.BodyOpen + 1;
                    i + 1 < caller.BodyClose; i++)
                {
                    if (t[i].Depth != 1 || !Word(t[i].Text) ||
                        t[i + 1].Text != "(" ||
                        blocked.Contains(t[i].Text))
                        continue;
                    var before = t[i - 1].Text;
                    if (before is not ("{" or ";" or "return" or "await"))
                        continue;
                    if (before == "{" && i - 1 != caller.BodyOpen)
                        continue;
                    var end = Closing(t, i + 1, "(", ")",
                        caller.BodyClose);
                    if (end < 0 || end + 1 >= caller.BodyClose ||
                        t[end + 1].Text != ";") continue;

                    AtlasNode? target = null;
                    if (localDirect.ContainsKey(t[i].Text))
                        target = FunctionNode(file.Path, t[i].Text);
                    else if (importsByName.TryGetValue(t[i].Text,
                        out var binding) && !binding.TypeOnly)
                    {
                        var (targetPath, _) = Resolve(file.Path,
                            binding.Module, files);
                        if (targetPath is not null &&
                            direct.TryGetValue(targetPath,
                                out var candidatesInModule) &&
                            candidatesInModule.Count(f =>
                                f.Name == binding.Name) == 1 &&
                            exported.TryGetValue(targetPath,
                                out var directExports) &&
                            directExports.TryGetValue(binding.Name,
                                out var exportNode) &&
                            exportNode.Kind == "Function")
                        {
                            var named = FunctionNode(targetPath,
                                binding.Name);
                            if (named?.Id == exportNode.Id)
                                target = named;
                        }
                    }
                    if (target is not null)
                        Link(callerNode.Id, target.Id, "Calls",
                            file.Path, t[i].Line,
                            "ExplicitEcmaDirectCallSyntax");
                }
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
