using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace BLearn.Core;

/// <summary>Parses one note file. Reports shape problems as issues; semantic checks live in <see cref="Validator"/>.</summary>
public static class NoteParser
{
    public static readonly string[] KnownFields = ["id", "title", "aliases", "status", "gloss", "edges", "sources"];

    public static Note? Parse(string file, string text, List<Issue> issues)
    {
        text = text.Replace("\r\n", "\n");
        void Err(string code, string msg) => issues.Add(new Issue(Severity.Error, code, file, msg));

        if (!text.StartsWith("---\n"))
        {
            Err("frontmatter-missing", "File must start with a '---' frontmatter block.");
            return null;
        }
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            Err("frontmatter-missing", "Frontmatter block is not closed with '---'.");
            return null;
        }
        var yaml = text[4..(end + 1)];
        var afterFence = text.IndexOf('\n', end + 1);
        var body = afterFence < 0 ? "" : text[(afterFence + 1)..];

        YamlMappingNode map;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode m)
            {
                Err("frontmatter-shape", "Frontmatter must be a YAML mapping.");
                return null;
            }
            map = m;
        }
        catch (YamlException ex)
        {
            Err("frontmatter-yaml", $"Invalid YAML: {ex.Message}");
            return null;
        }

        foreach (var key in map.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? ""))
            if (!KnownFields.Contains(key))
                Err("field-unknown", $"Unknown frontmatter field '{key}'. Allowed: {string.Join(", ", KnownFields)}.");

        string? Scalar(string key, bool required)
        {
            if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node))
            {
                if (required) Err("field-missing", $"Required field '{key}' is missing.");
                return null;
            }
            if (node is YamlScalarNode s && !string.IsNullOrWhiteSpace(s.Value)) return s.Value!.Trim();
            Err("field-shape", $"Field '{key}' must be a non-empty string.");
            return null;
        }

        List<string> List(string key)
        {
            if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node)) return [];
            if (node is YamlSequenceNode seq && seq.All(n => n is YamlScalarNode))
                return seq.Cast<YamlScalarNode>().Select(n => (n.Value ?? "").Trim()).ToList();
            Err("field-shape", $"Field '{key}' must be a list of strings.");
            return [];
        }

        var edges = new List<Edge>();
        if (map.Children.TryGetValue(new YamlScalarNode("edges"), out var edgesNode))
        {
            if (edgesNode is not YamlMappingNode edgeMap)
            {
                Err("field-shape", "Field 'edges' must map edge types to lists of note ids.");
            }
            else
            {
                foreach (var (k, v) in edgeMap.Children)
                {
                    var type = (k as YamlScalarNode)?.Value ?? "";
                    if (v is not YamlSequenceNode targets || !targets.All(t => t is YamlScalarNode))
                    {
                        Err("field-shape", $"Edge type '{type}' must map to a list of note ids.");
                        continue;
                    }
                    foreach (var t in targets.Cast<YamlScalarNode>())
                        edges.Add(new Edge(type, (t.Value ?? "").Trim()));
                }
            }
        }

        var id = Scalar("id", true);
        var title = Scalar("title", true);
        var status = Scalar("status", true);
        var gloss = Scalar("gloss", true);
        var aliases = List("aliases");
        var sources = List("sources");
        if (id is null || title is null || status is null || gloss is null) return null;

        var (preamble, sections) = SplitSections(body);
        return new Note
        {
            FilePath = file,
            Id = id,
            Title = title,
            Status = status,
            Gloss = gloss,
            Aliases = aliases,
            Edges = edges,
            Sources = sources,
            Preamble = preamble,
            Sections = sections,
        };
    }

    private static (string Preamble, List<Section> Sections) SplitSections(string body)
    {
        var sections = new List<Section>();
        var preamble = new List<string>();
        string? heading = null;
        var current = new List<string>();

        foreach (var (line, inFence) in Wikilinks.Lines(body))
        {
            if (!inFence && line.StartsWith("## "))
            {
                if (heading is not null) sections.Add(new Section(heading, string.Join('\n', current).Trim()));
                heading = line[3..].Trim();
                current = [];
                continue;
            }
            (heading is null ? preamble : current).Add(line);
        }
        if (heading is not null) sections.Add(new Section(heading, string.Join('\n', current).Trim()));
        return (string.Join('\n', preamble).Trim(), sections);
    }
}
