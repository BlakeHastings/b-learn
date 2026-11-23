using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BLearn.Domain;

public sealed record WikiLink(string Target, string? Label);

/// <summary>Finds and rewrites <c>[[id]]</c> and <c>[[id|label]]</c> links, skipping fenced code blocks.</summary>
public static partial class Wikilinks
{
    [GeneratedRegex(@"\[\[([^\[\]|]+)(?:\|([^\[\]]+))?\]\]")]
    private static partial Regex LinkRegex();

    public static IEnumerable<WikiLink> Scan(string markdown)
    {
        foreach (var (line, inFence) in Lines(markdown))
        {
            if (inFence) continue;
            foreach (Match m in LinkRegex().Matches(line))
                yield return ToLink(m);
        }
    }

    public static string Replace(string markdown, Func<WikiLink, string> render)
    {
        var sb = new StringBuilder();
        foreach (var (line, inFence) in Lines(markdown))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(inFence ? line : LinkRegex().Replace(line, m => render(ToLink(m))));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Turns the first plain mention of each named concept into a <c>[[id|mention]]</c> link. Longer names
    /// win, matching is case-insensitive on whole words, and existing links, inline code and fenced code
    /// are left alone. Concepts already linked explicitly are not linked again.
    /// </summary>
    public static string AutoLink(string markdown, IReadOnlyList<(string Name, string Id)> names)
    {
        var linked = Scan(markdown).Select(l => l.Target).ToHashSet();
        var candidates = names
            .Where(n => n.Name.Trim().Length >= 2 && !linked.Contains(n.Id))
            .OrderByDescending(n => n.Name.Length)
            .ToList();
        if (candidates.Count == 0) return markdown;

        var pattern = string.Join("|", candidates.Select(n => Regex.Escape(n.Name.Trim())));
        var mention = new Regex($@"(?<![\w#+.])(?:{pattern})(?![\w#+])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, id) in candidates) byName.TryAdd(name.Trim(), id);

        var sb = new StringBuilder();
        foreach (var (line, inFence) in Lines(markdown))
        {
            if (sb.Length > 0) sb.Append('\n');
            if (inFence) { sb.Append(line); continue; }
            // Split out existing links and inline code so only plain text is scanned.
            var parts = Protected().Split(line);
            foreach (var part in parts)
            {
                if (Protected().IsMatch(part)) { sb.Append(part); continue; }
                sb.Append(mention.Replace(part, m =>
                {
                    var id = byName[m.Value];
                    if (!linked.Add(id)) return m.Value;
                    return $"[[{id}|{m.Value}]]";
                }));
            }
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"(\[\[[^\]]*\]\]|`[^`]*`|\[[^\]]*\]\([^)]*\))")]
    private static partial Regex Protected();

    private static WikiLink ToLink(Match m) =>
        new(m.Groups[1].Value.Trim(), m.Groups[2].Success ? m.Groups[2].Value.Trim() : null);

    /// <summary>Yields each line with whether it is inside (or delimits) a fenced code block.</summary>
    public static IEnumerable<(string Line, bool InFence)> Lines(string markdown)
    {
        var inFence = false;
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inFence = !inFence;
                yield return (line, true);
                continue;
            }
            yield return (line, inFence);
        }
    }
}

/// <summary>Checks that a quote appears verbatim in source text, ignoring case, spacing, and typographic variants.</summary>
public static partial class QuoteMatcher
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static string Normalize(string text)
    {
        var s = text.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '‘' or '’' or '‛' or '′' => '\'',
                '“' or '”' or '‟' or '″' => '"',
                '‐' or '‑' or '‒' or '–' or '—' or '−' => '-',
                ' ' => ' ',
                _ => c,
            });
        }
        return Whitespace().Replace(sb.ToString(), " ").Trim();
    }

    public static bool Contains(string source, string quote)
    {
        var q = Normalize(quote);
        return q.Length > 0 && Normalize(source).Contains(q, StringComparison.Ordinal);
    }
}
