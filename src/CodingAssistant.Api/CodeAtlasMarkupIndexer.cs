using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

/// <summary>
/// M8.8: explicit, same-revision HTML-to-JS/CSS file references. The
/// importer executes no HTML/JS, fetches no dependency, and never guesses
/// links to generated output absent from the persisted source graph.
/// </summary>
public static class CodeAtlasMarkupIndexer
{
    private static readonly Regex Attributes = new(
        @"([A-Za-z][A-Za-z0-9-]*)\s*=\s*(?:""([^""]*)""|'([^']*)')",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ScriptExtensions = new(
        [".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CssExtensions = new(
        [".css"], StringComparer.OrdinalIgnoreCase);

    private static string WithoutComments(string source, out bool incomplete)
    {
        var chars = source.ToCharArray();
        incomplete = false;
        var position = 0;
        while (position < chars.Length)
        {
            var begin = source.IndexOf("<!--", position, StringComparison.Ordinal);
            if (begin < 0) break;
            var end = source.IndexOf("-->", begin + 4, StringComparison.Ordinal);
            if (end < 0)
            {
                end = source.Length;
                incomplete = true;
            }
            else end += 3;
            for (var i = begin; i < end; i++)
                if (chars[i] != '\n' && chars[i] != '\r') chars[i] = ' ';
            position = end;
        }
        return new string(chars);
    }

    private static string? Attribute(string tag, string name,
        out bool duplicated)
    {
        string? value = null;
        duplicated = false;
        foreach (Match match in Attributes.Matches(tag))
        {
            if (!string.Equals(match.Groups[1].Value, name,
                StringComparison.OrdinalIgnoreCase))
                continue;
            if (value is not null)
            {
                duplicated = true;
                return null;
            }
            value = match.Groups[2].Success ?
                match.Groups[2].Value : match.Groups[3].Value;
        }
        return value;
    }

    // The only meaningful target is an exact file in THIS indexed snapshot.
    // No URL normalization, fetching, extension guessing or bundler aliases.
    private static (string? Path, string? Error) Resolve(
        string importingPath, string literal,
        IReadOnlyDictionary<string, AtlasNode> files)
    {
        if (literal.Length is < 1 or > 200 ||
            literal.StartsWith("//", StringComparison.Ordinal) ||
            literal.Contains(':') || literal.Contains('\\') ||
            literal.Any(ch => !char.IsAsciiLetterOrDigit(ch) &&
                ch is not ('/' or '.' or '_' or '-')))
            return (null, null);
        var rooted = literal.StartsWith('/');
        var parts = rooted ? new List<string>() :
            importingPath.Split('/').SkipLast(1).ToList();
        foreach (var segment in literal.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (parts.Count == 0) return (null, "OUTSIDE_ROOT");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(segment);
        }
        var path = string.Join("/", parts);
        return files.ContainsKey(path) ? (path, null) :
            (null, "UNRESOLVED");
    }

    public static AtlasSnapshot Enrich(AtlasSnapshot graph,
        IReadOnlyList<EngineeringSourceFile> source)
    {
        var indexedFiles = graph.Nodes.Where(n =>
            n.Kind == "File" && n.Path is not null)
            .ToDictionary(n => n.Path!, StringComparer.Ordinal);
        var html = source.Where(f => indexedFiles.ContainsKey(f.Path) &&
            f.Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        if (html.Length == 0) return graph;

        var edges = graph.Edges.ToDictionary(e =>
            e.Kind + "|" + e.SourceId + "|" + e.TargetId,
            StringComparer.Ordinal);
        var diagnostics = graph.Diagnostics.ToList();
        var partial = false;
        void Problem(string code, string file, int line)
        {
            partial = true;
            if (diagnostics.Count < 80)
                diagnostics.Add("MARKUP_" + code + ":" + file + ":" + line);
        }

        foreach (var file in html)
        {
            var sourceText = WithoutComments(file.Content,
                out var unclosedComment);
            if (unclosedComment) Problem("UNCLOSED_COMMENT", file.Path, 1);
            var lineAt = new int[sourceText.Length + 1];
            var line = 1;
            for (var p = 0; p < sourceText.Length; p++)
            {
                lineAt[p] = line;
                if (sourceText[p] == '\n') line++;
            }
            lineAt[sourceText.Length] = line;

            var position = 0;
            var tags = 0;
            var references = 0;
            while (position < sourceText.Length)
            {
                var open = sourceText.IndexOf('<', position);
                if (open < 0) break;
                var cursor = open + 1;
                while (cursor < sourceText.Length &&
                    char.IsWhiteSpace(sourceText[cursor])) cursor++;
                if (cursor == sourceText.Length) break;
                if (!char.IsLetter(sourceText[cursor]))
                {
                    position = cursor + 1;
                    continue;
                }
                var beginName = cursor;
                while (cursor < sourceText.Length &&
                    (char.IsLetterOrDigit(sourceText[cursor]) ||
                     sourceText[cursor] is '-' or ':')) cursor++;
                var name = sourceText[beginName..cursor].ToLowerInvariant();
                var attributesStart = cursor;
                var quote = '\0';
                var validTag = true;
                while (cursor < sourceText.Length)
                {
                    var ch = sourceText[cursor];
                    if (quote != '\0')
                    {
                        if (ch == quote) quote = '\0';
                    }
                    else if (ch is '\'' or '"') quote = ch;
                    else if (ch == '>') break;
                    else if (ch == '<')
                    {
                        validTag = false;
                        break;
                    }
                    cursor++;
                    if (cursor - open > 1024)
                    {
                        validTag = false;
                        break;
                    }
                }
                if (!validTag || cursor == sourceText.Length)
                {
                    Problem("MALFORMED_TAG", file.Path, lineAt[open]);
                    position = Math.Min(cursor + 1, sourceText.Length);
                    continue;
                }
                var tag = sourceText[attributesStart..cursor];
                position = cursor + 1;
                if (++tags > 1000)
                {
                    Problem("TAG_LIMIT", file.Path, lineAt[open]);
                    break;
                }
                var isScript = name == "script";
                var isStyleLink = name == "link";
                if (isScript || isStyleLink)
                {
                    var rel = isStyleLink ? Attribute(tag, "rel",
                        out var duplicateRel) : null;
                    if (isStyleLink && duplicateRel)
                    {
                        Problem("DUPLICATE_ATTRIBUTE", file.Path, lineAt[open]);
                        continue;
                    }
                    var stylesheet = isStyleLink && rel is not null &&
                        rel.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Contains("stylesheet", StringComparer.OrdinalIgnoreCase);
                    if (isScript || stylesheet)
                    {
                        var literal = Attribute(tag,
                            isScript ? "src" : "href", out var duplicate);
                        if (duplicate)
                            Problem("DUPLICATE_ATTRIBUTE", file.Path, lineAt[open]);
                        else if (literal is not null)
                        {
                            var extensions = isScript ?
                                ScriptExtensions : CssExtensions;
                            if (extensions.Contains(Path.GetExtension(literal)))
                            {
                                var (target, problem) = Resolve(
                                    file.Path, literal, indexedFiles);
                                if (problem is not null)
                                    Problem("LOCAL_" + problem, file.Path,
                                        lineAt[open]);
                                else if (target is not null)
                                {
                                    var from = indexedFiles[file.Path].Id;
                                    var to = indexedFiles[target].Id;
                                    var key = "MarkupReferences|" + from +
                                        "|" + to;
                                    if (edges.Count >= CodeAtlasIndexer.MaximumEdges)
                                        Problem("EDGE_LIMIT", file.Path,
                                            lineAt[open]);
                                    else
                                    {
                                        edges.TryAdd(key, new AtlasEdge(from, to,
                                            "MarkupReferences", file.Path,
                                            lineAt[open], "LiteralMarkupReference"));
                                        if (++references > 128)
                                        {
                                            Problem("REFERENCE_LIMIT",
                                                file.Path, lineAt[open]);
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                // Script text is a raw-text element, not another sequence
                // of HTML tags. Never parse "script src=..." inside JS text.
                if (isScript || name is "style" or "textarea" or "template")
                {
                    var closeName = "</" + name;
                    var ending = sourceText.IndexOf(closeName, position,
                        StringComparison.OrdinalIgnoreCase);
                    if (ending < 0)
                    {
                        Problem("UNCLOSED_RAW_TEXT", file.Path, lineAt[open]);
                        break;
                    }
                    var greater = sourceText.IndexOf('>', ending);
                    if (greater < 0)
                    {
                        Problem("UNCLOSED_RAW_TEXT", file.Path, lineAt[open]);
                        break;
                    }
                    position = greater + 1;
                }
            }
        }
        var other = source.Any(f => indexedFiles.ContainsKey(f.Path) &&
            !f.Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
            (f.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".java", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)));
        var coverage = partial ? "PARTIAL_MARKUP_STATIC_REFERENCES" :
            "MARKUP_STATIC_REFERENCES_ONLY";
        if (other) coverage = graph.Coverage + "+" + coverage;
        return graph with
        {
            Coverage = coverage,
            Edges = edges.Values.OrderBy(e => e.Kind, StringComparer.Ordinal)
                .ThenBy(e => e.SourceId, StringComparer.Ordinal)
                .ThenBy(e => e.TargetId, StringComparer.Ordinal).ToArray(),
            Diagnostics = diagnostics.AsReadOnly()
        };
    }
}
