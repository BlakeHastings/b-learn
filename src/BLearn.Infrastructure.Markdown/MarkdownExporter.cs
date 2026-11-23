using System.Text;
using BLearn.Application;

namespace BLearn.Infrastructure.Markdown;

/// <summary>
/// One-way export of a store to markdown, one file per concept, readable in Obsidian.
/// The export is a copy: edits to it are never read back.
/// </summary>
public static class MarkdownExporter
{
    public static int Export(IQueries queries, string folder)
    {
        var notesDir = Path.Combine(folder, "notes");
        Directory.CreateDirectory(notesDir);
        foreach (var old in Directory.EnumerateFiles(notesDir, "*.md")) File.Delete(old);

        var info = queries.GetInfo();
        File.WriteAllText(Path.Combine(folder, "README.md"),
            $"# {info.Name}\n\nExported from `{info.Path}` on {DateTimeOffset.Now:yyyy-MM-dd HH:mm}. This is a read-only copy; " +
            $"change the store with blearn commands.\n\nStart at [[{info.Root}]].\n");

        var count = 0;
        foreach (var summary in queries.ListConcepts())
        {
            var c = queries.GetConcept(summary.Id)!;
            File.WriteAllText(Path.Combine(notesDir, c.Id + ".md"), Render(c));
            count++;
        }
        return count;
    }

    public static string Render(ConceptDetail c)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append($"id: {c.Id}\n");
        sb.Append($"title: {Yaml(c.Title)}\n");
        if (c.Aliases.Count > 0) sb.Append($"aliases: [{string.Join(", ", c.Aliases.Select(Yaml))}]\n");
        sb.Append($"status: {c.Status}\n");
        sb.Append($"gloss: {Yaml(c.Gloss)}\n");
        var outgoing = c.Neighbors.Where(n => n.Label == n.EdgeType).GroupBy(n => n.EdgeType).ToList();
        if (outgoing.Count > 0)
        {
            sb.Append("edges:\n");
            foreach (var g in outgoing) sb.Append($"  {g.Key}: [{string.Join(", ", g.Select(n => n.Id))}]\n");
        }
        sb.Append("---\n");

        if (c.Summary is not null) sb.Append($"\n## Summary\n\n{c.Summary}\n");
        if (c.Explanation is not null) sb.Append($"\n## Explanation\n\n{c.Explanation}\n");
        if (c.TeachingLegacy) sb.Append("\n> [!warning] Unverified\n> This teaching text was imported and is not backed by cited facts.\n");

        var evidence = c.Facts.SelectMany(f => f.Evidence).DistinctBy(e => e.Id).ToList();
        if (c.Facts.Count > 0)
        {
            sb.Append("\n## Facts\n\n");
            foreach (var f in c.Facts)
                sb.Append($"- {f.Text}{string.Concat(f.Evidence.Select(e => $"[^{e.Id}]"))}\n");
            sb.Append('\n');
            foreach (var e in evidence)
                sb.Append($"[^{e.Id}]: \"{e.Quote}\" ({e.Source}, {e.Locator}, {e.Trust}{(e.Stale ? ", stale" : "")})\n");
        }

        var related = c.Neighbors.GroupBy(n => n.Direction).OrderBy(g => g.Key switch { "down" => 0, "lateral" => 1, _ => 2 });
        sb.Append("\n## Related\n\n");
        foreach (var g in related)
            foreach (var n in g)
                sb.Append($"- {g.Key}: [[{n.Id}|{n.Title}]] ({n.Label}{(n.Legacy ? ", unverified" : "")})\n");
        return sb.ToString();
    }

    private static string Yaml(string s) => s.IndexOfAny([':', '#', '[', ']', '{', '}', ',', '"', '\'']) >= 0 ? $"\"{s.Replace("\"", "\\\"")}\"" : s;
}
