namespace BLearn.Domain;

/// <summary>Where a neighbor sits relative to the concept you are looking from.</summary>
public enum Direction
{
    Down,
    Up,
    Lateral,
}

/// <summary>A relationship kind. Its direction is fixed by the type, never chosen per edge.</summary>
/// <param name="Name">Key used in commands and storage.</param>
/// <param name="Direction">Direction from the source concept to the target concept.</param>
/// <param name="InverseLabel">Label shown when the edge is viewed from the target.</param>
/// <param name="Meaning">One-line definition.</param>
public sealed record EdgeType(string Name, Direction Direction, string InverseLabel, string Meaning);

public static class EdgeTypes
{
    public static readonly IReadOnlyList<EdgeType> List =
    [
        new("is-a", Direction.Down, "generalizes", "Source is a specific kind of target."),
        new("built-on", Direction.Down, "underlies", "Source is implemented on top of target, a lower abstraction layer."),
        new("composed-of", Direction.Down, "part-of", "Target is a component of source."),
        new("requires", Direction.Down, "required-by", "Understanding source requires understanding target."),
        new("contrasts-with", Direction.Lateral, "contrasts-with", "Source and target are alternatives or opposites worth comparing."),
        new("related", Direction.Lateral, "related", "Source and target are connected at the same level."),
        new("created-by", Direction.Lateral, "created", "Target (a person or organization) created source."),
        new("implemented-in", Direction.Lateral, "implements", "Source is written in target, as with a self-hosting compiler."),
    ];

    public static readonly IReadOnlyDictionary<string, EdgeType> All = List.ToDictionary(e => e.Name);

    public static IEnumerable<string> DownNames => List.Where(e => e.Direction == Direction.Down).Select(e => e.Name);

    public static EdgeType Require(string name) =>
        All.TryGetValue(name, out var t)
            ? t
            : throw new DomainViolation("edge-type", $"Unknown edge type '{name}'. Allowed: {string.Join(", ", All.Keys)}.");

    public static Direction Invert(Direction d) => d switch
    {
        Direction.Down => Direction.Up,
        Direction.Up => Direction.Down,
        _ => Direction.Lateral,
    };
}
