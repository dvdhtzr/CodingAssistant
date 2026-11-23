using System.Xml;
using System.Xml.Linq;

namespace CodingAssistant.Api;

/// <summary>
/// Reads literal, unconditioned MSBuild ProjectReference elements as data.
/// It never evaluates project files, imports, targets or property expressions.
/// </summary>
public static class CodeAtlasProjectReferences
{
    private static string? Resolve(string projectFile, string include)
    {
        var value = include.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') ||
            value.Contains(':') || value.Contains('$') ||
            value.Any(char.IsControl))
            return null;
        var stack = projectFile.Split('/').SkipLast(1).ToList();
        foreach (var item in value.Split('/'))
        {
            if (item == ".") continue;
            if (item == "..")
            {
                if (stack.Count == 0) return null;
                stack.RemoveAt(stack.Count - 1);
            }
            else if (item.Length == 0)
                return null;
            else
                stack.Add(item);
        }
        return string.Join("/", stack);
    }

    public static AtlasSnapshot Enrich(AtlasSnapshot graph,
        IReadOnlyList<EngineeringSourceFile> files)
    {
        var projects = graph.Nodes.Where(node => node.Kind == "Project" &&
            node.Path is not null).ToDictionary(node => node.Path!,
            StringComparer.Ordinal);
        if (projects.Count == 0) return graph;

        var edges = graph.Edges.ToDictionary(
            e => e.Kind + "|" + e.SourceId + "|" + e.TargetId,
            e => e, StringComparer.Ordinal);
        var diagnostics = graph.Diagnostics.ToList();

        foreach (var file in files.Where(f => projects.ContainsKey(f.Path)))
        {
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 120001
                };
                using var content = new StringReader(file.Content);
                using var reader = XmlReader.Create(content, settings);
                var project = XDocument.Load(reader, LoadOptions.SetLineInfo);
                foreach (var reference in project.Descendants()
                    .Where(element => element.Name.LocalName == "ProjectReference"))
                {
                    var literal = reference.Attribute("Include")?.Value;
                    if (literal is null) continue;
                    // A conditional or computed reference is NOT proven active.
                    if (reference.AncestorsAndSelf().Any(element =>
                        element.Attribute("Condition") is not null))
                    {
                        diagnostics.Add("CONDITIONAL_PROJECT_REFERENCE_UNRESOLVED:" +
                            file.Path);
                        continue;
                    }
                    var path = Resolve(file.Path, literal);
                    if (path is null || !projects.TryGetValue(path, out var to))
                        continue;
                    var from = projects[file.Path];
                    if (from.Id == to.Id) continue;
                    var key = "ProjectReferences|" + from.Id + "|" + to.Id;
                    if (!edges.ContainsKey(key) &&
                        edges.Count < CodeAtlasIndexer.MaximumEdges)
                    {
                        var line = (reference as IXmlLineInfo)?.LineNumber;
                        edges.Add(key, new(from.Id, to.Id, "ProjectReferences",
                            file.Path, line, "LiteralProjectReference"));
                    }
                }
            }
            catch (XmlException)
            {
                diagnostics.Add("INVALID_PROJECT_XML:" + file.Path);
            }
        }
        return graph with
        {
            Edges = edges.Values.OrderBy(e => e.Kind, StringComparer.Ordinal)
                .ThenBy(e => e.SourceId, StringComparer.Ordinal)
                .ThenBy(e => e.TargetId, StringComparer.Ordinal).ToArray(),
            Diagnostics = diagnostics.AsReadOnly()
        };
    }
}
