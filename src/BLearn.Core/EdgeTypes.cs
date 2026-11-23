namespace BLearn.Core;

/// <summary>Where a neighbor sits relative to the note you are reading.</summary>
public enum Direction
{
    Down,
    Up,
    Lateral,
}

/// <summary>A relationship kind. Its direction is fixed by the type, never chosen per edge.</summary>
/// <param name="Name">Key used in note frontmatter.</param>
/// <param name="Direction">Direction from the source note to the target note.</param>
/// <param name="Symmetric">True when A-to-B means the same as B-to-A, so it may only be stored once.</param>
/// <param name="InverseLabel">Label shown when the edge is viewed from the target note.</param>
/// <param name="Meaning">One-line definition, shown in docs and tooling.</param>
public sealed record EdgeType(string Name, Direction Direction, bool Symmetric, string InverseLabel, string Meaning);

public static class EdgeTypes
{
    public static readonly IReadOnlyDictionary<string, EdgeType> All = new EdgeType[]
    {
        new("is-a", Direction.Down, false, "generalizes", "Source is a specific kind of target."),
        new("built-on", Direction.Down, false, "underlies", "Source is implemented on top of target, a lower abstraction layer."),
        new("composed-of", Direction.Down, false, "part-of", "Target is a component of source."),
        new("requires", Direction.Down, false, "required-by", "Understanding source requires understanding target."),
        new("contrasts-with", Direction.Lateral, true, "contrasts-with", "Source and target are alternatives or opposites worth comparing."),
        new("related", Direction.Lateral, true, "related", "Source and target are connected at the same level."),
        new("created-by", Direction.Lateral, false, "created", "Target (a person or organization) created source."),
    }.ToDictionary(e => e.Name);

    public static Direction Invert(Direction d) => d switch
    {
        Direction.Down => Direction.Up,
        Direction.Up => Direction.Down,
        _ => Direction.Lateral,
    };
}
