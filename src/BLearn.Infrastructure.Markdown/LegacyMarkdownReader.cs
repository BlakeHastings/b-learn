using BLearn.Application;
using BLearn.Domain;
using YamlDotNet.RepresentationModel;

namespace BLearn.Infrastructure.Markdown;

/// <summary>
/// Reads a prototype markdown store (<c>notes/*.md</c> with YAML frontmatter) into <see cref="LegacyNote"/>s.
/// Shape errors are reported, not thrown, so one bad file does not hide the rest.
/// </summary>
public static class LegacyMarkdownReader
{
    public static (List<LegacyNote> Notes, List<string> Problems) ReadFolder(string folder)
    {
        var notesDir = Directory.Exists(Path.Combine(folder, "notes")) ? Path.Combine(folder, "notes") : folder;
        var notes = new List<LegacyNote>();
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(notesDir, "*.md").Order())
        {
            try { notes.Add(Parse(File.ReadAllText(file))); }
            catch (Exception ex) when (ex is FormatException or YamlDotNet.Core.YamlException)
            {
                problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return (notes, problems);
    }

    public static LegacyNote Parse(string text)
    {
        text = text.Replace("\r\n", "\n");
        if (!text.StartsWith("---\n")) throw new FormatException("Missing frontmatter.");
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) throw new FormatException("Unclosed frontmatter.");
        var yaml = text[4..(end + 1)];
        var after = text.IndexOf('\n', end + 1);
        var body = after < 0 ? "" : text[(after + 1)..];

        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var map = stream.Documents[0].RootNode as YamlMappingNode ?? throw new FormatException("Frontmatter is not a mapping.");

        string Get(string key) =>
            map.Children.TryGetValue(new YamlScalarNode(key), out var n) && n is YamlScalarNode s && s.Value is { } v
                ? v.Trim() : throw new FormatException($"Missing '{key}'.");
        List<string> List(YamlNode? n) => n is YamlSequenceNode seq ? seq.OfType<YamlScalarNode>().Select(x => (x.Value ?? "").Trim()).ToList() : [];

        var aliases = map.Children.TryGetValue(new YamlScalarNode("aliases"), out var an) ? List(an) : [];
        var edges = new List<(string, string)>();
        if (map.Children.TryGetValue(new YamlScalarNode("edges"), out var en) && en is YamlMappingNode em)
            foreach (var (k, v) in em.Children)
                foreach (var target in List(v))
                    edges.Add(((k as YamlScalarNode)?.Value ?? "", target));

        var sections = Sections(body);
        return new LegacyNote(Get("id"), Get("title"), Get("gloss"), aliases, Get("status") == "stub",
            sections.GetValueOrDefault("Summary"), sections.GetValueOrDefault("Detail"), edges);
    }

    private static Dictionary<string, string> Sections(string body)
    {
        var result = new Dictionary<string, string>();
        string? heading = null;
        var current = new List<string>();
        foreach (var (line, inFence) in Wikilinks.Lines(body))
        {
            if (!inFence && line.StartsWith("## "))
            {
                if (heading is not null) result[heading] = string.Join('\n', current).Trim();
                heading = line[3..].Trim();
                current = [];
                continue;
            }
            current.Add(line);
        }
        if (heading is not null) result[heading] = string.Join('\n', current).Trim();
        return result;
    }
}
