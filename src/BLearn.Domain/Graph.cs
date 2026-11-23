namespace BLearn.Domain;

/// <summary>
/// The relationships between concepts in a store. Its invariants span the whole graph, so it
/// consults <see cref="IGraphReader"/> instead of loading every edge.
/// </summary>
public sealed class Graph(IGraphReader reader) : AggregateRoot
{
    /// <summary>Adds a relationship. New edges must cite evidence; <paramref name="legacy"/> marks imported, unverified edges.</summary>
    public void AddEdge(string source, string type, string target, IReadOnlyList<string> evidenceIds, bool legacy = false)
    {
        var edgeType = EdgeTypes.Require(type);
        if (source == target) throw new DomainViolation("edge-self", "A concept cannot relate to itself.");
        foreach (var id in new[] { source, target })
            if (!reader.ConceptExists(id))
                throw new DomainViolation("edge-dangling", $"Concept '{id}' does not exist. Create it first (a stub is fine).");

        if (reader.EdgeBetween(source, target) is { } existing)
            throw new DomainViolation("edge-multiple",
                $"'{existing.Source}' already relates to '{existing.Target}' ({existing.Type}). A pair of concepts has exactly one relationship.");

        var evidence = CheckEvidence(evidenceIds, required: !legacy);

        if (edgeType.Direction == Direction.Down && reader.DownPathExists(target, source))
            throw new DomainViolation("edge-cycle",
                $"'{target}' already leads down to '{source}', so this edge would make a downward loop. " +
                "A loop usually means a wrong edge type (consider a sideways type such as implemented-in) or a concept that needs splitting.");

        Raise(new EdgeAdded(source, edgeType.Name, target, evidence, legacy));
    }

    public void RemoveEdge(string source, string target)
    {
        var existing = reader.EdgeBetween(source, target);
        if (existing is null || existing.Source != source)
            throw new DomainViolation("edge-missing", $"No edge from '{source}' to '{target}'.");
        Raise(new EdgeRemoved(source, target));
    }

    /// <summary>Adds evidence to an existing edge, which verifies a legacy edge.</summary>
    public void AttachEvidence(string source, string target, IReadOnlyList<string> evidenceIds)
    {
        var existing = reader.EdgeBetween(source, target);
        if (existing is null || existing.Source != source)
            throw new DomainViolation("edge-missing", $"No edge from '{source}' to '{target}'.");
        Raise(new EdgeEvidenceAttached(source, target, CheckEvidence(evidenceIds, required: true)));
    }

    private List<string> CheckEvidence(IReadOnlyList<string> evidenceIds, bool required)
    {
        var evidence = evidenceIds.Select(e => e.Trim()).Where(e => e.Length > 0).Distinct().ToList();
        if (required && evidence.Count == 0)
            throw new DomainViolation("edge-uncited", "A relationship is a claim and must cite evidence.");
        foreach (var e in evidence)
            if (!reader.EvidenceExists(e))
                throw new DomainViolation("evidence-missing", $"Unknown evidence '{e}'. Capture it first.");
        return evidence;
    }
}
