namespace BLearn.Core;

public enum Severity
{
    Error,
    Warning,
}

public sealed record Issue(Severity Severity, string Code, string File, string Message)
{
    public override string ToString() => $"{Severity.ToString().ToUpperInvariant()} {Code} {File}: {Message}";
}

public sealed record Edge(string Type, string Target);

public sealed record Section(string Heading, string Content);

public sealed class Note
{
    public required string FilePath { get; init; }
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    public required string Gloss { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public IReadOnlyList<Edge> Edges { get; init; } = [];
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>Text between the frontmatter and the first H2 heading.</summary>
    public string Preamble { get; init; } = "";

    public IReadOnlyList<Section> Sections { get; init; } = [];

    public bool IsStub => Status == NoteStatus.Stub;

    public string? SectionText(string heading) =>
        Sections.FirstOrDefault(s => s.Heading == heading)?.Content;
}

public static class NoteStatus
{
    public const string Stub = "stub";
    public const string Complete = "complete";
    public static readonly string[] All = [Stub, Complete];
}

public sealed record Project(string Name, string Root);
