namespace BLearn.Core;

/// <summary>A note seen from another note: which way it lies and what the edge is called from this side.</summary>
public sealed record Neighbor(Note Note, string EdgeType, Direction Direction, string Label);

/// <summary>Read-side view over a store's notes. Edges are stored once on the source note; this exposes both sides.</summary>
public sealed class Graph
{
    private readonly Dictionary<string, Note> _byId;
    private readonly Dictionary<string, List<Neighbor>> _neighbors = new();

    public Graph(IEnumerable<Note> notes)
    {
        _byId = notes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var note in _byId.Values)
        {
            foreach (var edge in note.Edges)
            {
                if (!EdgeTypes.All.TryGetValue(edge.Type, out var type)) continue;
                if (!_byId.TryGetValue(edge.Target, out var target)) continue;
                Add(note.Id, new Neighbor(target, type.Name, type.Direction, type.Name));
                Add(target.Id, new Neighbor(note, type.Name, EdgeTypes.Invert(type.Direction), type.InverseLabel));
            }
        }
    }

    public Note? Find(string id) => _byId.GetValueOrDefault(id);

    public IReadOnlyList<Neighbor> NeighborsOf(string id) =>
        _neighbors.TryGetValue(id, out var list) ? list : [];

    /// <summary>How <paramref name="toId"/> relates to <paramref name="fromId"/>, or null when they share no edge.</summary>
    public Neighbor? Relation(string fromId, string toId) =>
        NeighborsOf(fromId).FirstOrDefault(n => n.Note.Id == toId);

    private void Add(string id, Neighbor n)
    {
        if (!_neighbors.TryGetValue(id, out var list)) _neighbors[id] = list = [];
        list.Add(n);
    }
}
