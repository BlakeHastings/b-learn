using System.Text.RegularExpressions;

namespace BLearn.Domain;

/// <summary>A command broke a domain invariant. <see cref="Code"/> is stable and machine-readable.</summary>
public sealed class DomainViolation(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static partial class Ids
{
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KebabRegex();

    public static bool IsKebab(string value) => KebabRegex().IsMatch(value);

    public static string Require(string value, string what)
    {
        if (!IsKebab(value))
            throw new DomainViolation("id-format", $"{what} '{value}' must be lowercase kebab-case.");
        return value;
    }

    /// <summary>Derives a kebab-case id from a title, for example "C#" becomes "c-sharp".</summary>
    public static string FromTitle(string title)
    {
        var s = title.Trim().ToLowerInvariant()
            .Replace("c#", "c-sharp").Replace("f#", "f-sharp").Replace("++", "-plus-plus").Replace(".net", "dotnet");
        s = Regex.Replace(s, "[^a-z0-9]+", "-").Trim('-');
        return s.Length == 0 ? "concept" : s;
    }
}

public static class Names
{
    /// <summary>Normalizes a title or alias for duplicate detection: case, spacing, hyphens and underscores ignored.</summary>
    public static string Normalize(string name) =>
        Regex.Replace(name.Trim().ToLowerInvariant(), @"[\s_-]+", " ");
}

public static class Gloss
{
    public const int MaxLength = 200;

    public static string Require(string gloss)
    {
        gloss = gloss.Trim();
        if (gloss.Length == 0) throw new DomainViolation("gloss-format", "Gloss is required.");
        if (gloss.Contains('\n')) throw new DomainViolation("gloss-format", "Gloss must be a single line.");
        if (gloss.Length > MaxLength) throw new DomainViolation("gloss-format", $"Gloss is {gloss.Length} chars; max is {MaxLength}.");
        if (gloss.Contains("[[")) throw new DomainViolation("gloss-format", "Gloss must not contain links.");
        return gloss;
    }
}

/// <summary>Base for aggregates: tracks a version for optimistic concurrency and the events a command produced.</summary>
public abstract class AggregateRoot
{
    private readonly List<DomainEvent> _pending = [];

    /// <summary>Version as loaded from storage. Zero for a new aggregate.</summary>
    public long Version { get; protected set; }

    public IReadOnlyList<DomainEvent> PendingEvents => _pending;

    protected void Raise(DomainEvent e) => _pending.Add(e);

    /// <summary>Called by a repository after it stored the pending events: records the new version and clears them.</summary>
    public void MarkPersisted(long newVersion)
    {
        Version = newVersion;
        _pending.Clear();
    }
}
