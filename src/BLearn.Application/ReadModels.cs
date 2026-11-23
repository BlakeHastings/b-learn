namespace BLearn.Application;

public sealed record SourceView(string Name, string System, string Scope, string MinTrust);

public sealed record StoreInfo(
    string Path, string Name, string Root, bool Private, IReadOnlyList<SourceView> Sources,
    IReadOnlyDictionary<string, int> ConceptsByStatus, int Evidence, int StaleEvidence, int LegacyEdges, long LatestEventSeq);

public sealed record ConceptSummary(string Id, string Title, string Gloss, string Status);

public sealed record EvidenceView(
    string Id, string Source, string SourceSystem, string Locator, string Quote, string Trust, bool Stale,
    bool MeetsMinTrust, string CapturedBy, string? ReconfirmedBy, DateTimeOffset RetrievedAt, string SnapshotHash);

/// <param name="Verified">True when at least one cited piece of evidence is current and meets its source's minimum trust.</param>
public sealed record FactView(int Index, string Text, IReadOnlyList<EvidenceView> Evidence, bool Verified);

/// <summary>A neighbor as seen from a concept. Direction and label are from the viewing concept's side.</summary>
public sealed record NeighborView(
    string Id, string Title, string Gloss, string Status, string Direction, string Label, string EdgeType,
    bool Legacy, IReadOnlyList<string> EvidenceIds);

public sealed record ConceptDetail(
    string Id, string Title, string Gloss, string Status, IReadOnlyList<string> Aliases,
    string? Summary, string? Explanation, bool TeachingLegacy, IReadOnlyList<FactView> Facts,
    IReadOnlyList<NeighborView> Neighbors, IReadOnlyList<QueueMessage> OpenWork);

public sealed record GraphNode(string Id, string Title, string Status, int Hops);

/// <summary>An edge as stored: direction is from <see cref="Source"/> to <see cref="Target"/>.</summary>
public sealed record GraphEdge(string Source, string Target, string Type, string Direction, bool Legacy);

public sealed record GraphView(string Focus, IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);

public sealed record VisitView(long Seq, DateTimeOffset At, string ConceptId, string? FromConceptId, string? Direction);

/// <summary>Read side. Queries run outside command transactions and return view models.</summary>
public interface IQueries
{
    StoreInfo GetInfo();
    ConceptDetail? GetConcept(string id);
    IReadOnlyList<ConceptSummary> ListConcepts(string? status = null);
    IReadOnlyList<ConceptSummary> Search(string text, int limit = 20);
    IReadOnlyList<NeighborView> Neighbors(string id);

    /// <summary>How <paramref name="to"/> relates to <paramref name="from"/>, or null when no edge connects them.</summary>
    NeighborView? Relation(string from, string to);

    GraphView GraphAround(string focus, int hops);
    EvidenceView? GetEvidence(string id);
    IReadOnlyList<EvidenceView> ListEvidence();
    Snapshot? GetSnapshot(string hash);
    IReadOnlyList<VisitView> Journey(int limit = 100);
    IReadOnlyList<string> Known();
}
