namespace BLearn.Domain;

public sealed record EdgeRecord(string Source, string Type, string Target);

/// <summary>
/// Narrow read access to store-wide state, for invariants that span more than one aggregate
/// (unique names, no downward cycles). Implementations must answer from the same transaction
/// the command will commit in.
/// </summary>
public interface IGraphReader
{
    bool ConceptExists(string id);

    /// <summary>The concept that owns a normalized name, or null when the name is free.</summary>
    string? NameOwner(string normalizedName);

    /// <summary>The edge between two concepts in either direction, or null.</summary>
    EdgeRecord? EdgeBetween(string a, string b);

    /// <summary>True when following downward edges from <paramref name="from"/> reaches <paramref name="to"/>.</summary>
    bool DownPathExists(string from, string to);

    bool EvidenceExists(string id);

    /// <summary>True when any fact or edge cites the evidence.</summary>
    bool EvidenceCited(string id);

    /// <summary>The session that captured the evidence, or null when it does not exist.</summary>
    string? EvidenceCapturedBy(string id);
}
