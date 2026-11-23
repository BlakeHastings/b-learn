using System.Text;
using System.Text.RegularExpressions;

namespace BLearn.Core;

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
            sb.Append(inFence ? line : LinkRegex().Replace(line, m => render(ToLink(m))));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static WikiLink ToLink(Match m) =>
        new(m.Groups[1].Value.Trim(), m.Groups[2].Success ? m.Groups[2].Value.Trim() : null);

    /// <summary>Yields each line with whether it is inside (or delimits) a fenced code block.</summary>
    internal static IEnumerable<(string Line, bool InFence)> Lines(string markdown)
    {
        var inFence = false;
        foreach (var line in markdown.Split('\n'))
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
