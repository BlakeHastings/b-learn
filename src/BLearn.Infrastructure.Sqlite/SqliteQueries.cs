using BLearn.Application;
using BLearn.Domain;
using Microsoft.Data.Sqlite;

namespace BLearn.Infrastructure.Sqlite;

internal sealed class SqliteQueries(string path) : IQueries
{
    private T With<T>(Func<SqliteConnection, T> f)
    {
        using var c = Sql.Open(path);
        return f(c);
    }

    public StoreInfo GetInfo() => With(c =>
    {
        var meta = c.Query("SELECT key, value FROM meta", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
        var sources = c.Query("SELECT name, system, scope, min_trust FROM sources ORDER BY name",
            r => new SourceView(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        var byStatus = AllSummaries(c).GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Count());
        return new StoreInfo(
            path, meta.GetValueOrDefault("name", "(uninitialized)"), meta.GetValueOrDefault("root", ""), meta.GetValueOrDefault("private") == "1",
            sources, byStatus,
            Convert.ToInt32(c.Scalar("SELECT count(*) FROM evidence")),
            Convert.ToInt32(c.Scalar("SELECT count(*) FROM evidence WHERE stale = 1")),
            Convert.ToInt32(c.Scalar("SELECT count(*) FROM edges WHERE legacy = 1")),
            Convert.ToInt64(c.Scalar("SELECT coalesce(max(seq), 0) FROM events")));
    });

    public ConceptDetail? GetConcept(string id) => With(c =>
    {
        var row = c.Query("SELECT id, title, gloss, summary, explanation, teaching_legacy FROM concepts WHERE id = @id",
            r => (Id: r.GetString(0), Title: r.GetString(1), Gloss: r.GetString(2), Summary: r.NullableString(3),
                Explanation: r.NullableString(4), Legacy: r.GetInt64(5) != 0), ("@id", id)).FirstOrDefault();
        if (row.Id is null) return null;

        var aliases = c.Query("SELECT name FROM concept_names WHERE concept_id = @id AND kind = 'alias' ORDER BY name", r => r.GetString(0), ("@id", id));
        var evidence = EvidenceViews(c, "WHERE e.id IN (SELECT evidence_id FROM fact_evidence WHERE concept_id = @id)", ("@id", id))
            .ToDictionary(e => e.Id);
        var links = c.Query("SELECT ordinal, evidence_id FROM fact_evidence WHERE concept_id = @id ORDER BY evidence_id",
            r => (Ordinal: (int)r.GetInt64(0), Evidence: r.GetString(1)), ("@id", id)).ToLookup(x => x.Ordinal, x => evidence[x.Evidence]);
        var facts = c.Query("SELECT ordinal, text FROM facts WHERE concept_id = @id ORDER BY ordinal", r =>
        {
            var ev = links[(int)r.GetInt64(0)].ToList();
            return new FactView((int)r.GetInt64(0), r.GetString(1), ev, ev.Any(e => !e.Stale && e.MeetsMinTrust));
        }, ("@id", id));
        var work = OpenWork(c, id);
        var status = StatusOf(facts.Count > 0, row.Summary is not null);
        return new ConceptDetail(row.Id, row.Title, row.Gloss, status, aliases, row.Summary, row.Explanation, row.Legacy, facts,
            NeighborsOf(c, id), work);
    });

    public IReadOnlyList<ConceptSummary> ListConcepts(string? status = null) =>
        With(c => AllSummaries(c).Where(s => status is null || s.Status == status).ToList());

    public IReadOnlyList<ConceptSummary> Search(string text, int limit = 20) => With(c =>
    {
        var like = $"%{text.Trim().ToLowerInvariant()}%";
        var ids = c.Query("""
            SELECT DISTINCT c.id FROM concepts c
            LEFT JOIN concept_names n ON n.concept_id = c.id
            WHERE lower(n.name) LIKE @q OR lower(c.title) LIKE @q OR lower(c.gloss) LIKE @q
            ORDER BY c.id LIMIT @lim
            """, r => r.GetString(0), ("@q", like), ("@lim", limit)).ToHashSet();
        return AllSummaries(c).Where(s => ids.Contains(s.Id)).ToList();
    });

    public IReadOnlyList<NeighborView> Neighbors(string id) => With(c => NeighborsOf(c, id));

    public NeighborView? Relation(string from, string to) => Neighbors(from).FirstOrDefault(n => n.Id == to);

    public GraphView GraphAround(string focus, int hops) => With(c =>
    {
        var nodes = new Dictionary<string, int> { [focus] = 0 };
        var frontier = new List<string> { focus };
        for (var h = 1; h <= hops && frontier.Count > 0; h++)
        {
            var (inClause, args) = Sql.In("f", frontier);
            var next = c.Query($"SELECT source, target FROM edges WHERE source IN {inClause} OR target IN {inClause}",
                r => (r.GetString(0), r.GetString(1)), args)
                .SelectMany(e => new[] { e.Item1, e.Item2 })
                .Where(n => !nodes.ContainsKey(n)).Distinct().ToList();
            foreach (var n in next) nodes[n] = h;
            frontier = next;
        }
        var summaries = AllSummaries(c).Where(s => nodes.ContainsKey(s.Id)).ToDictionary(s => s.Id);
        var (all, allArgs) = Sql.In("n", nodes.Keys);
        var edges = c.Query($"SELECT source, target, type, legacy FROM edges WHERE source IN {all} AND target IN {all}",
            r => new GraphEdge(r.GetString(0), r.GetString(1), r.GetString(2),
                EdgeTypes.All[r.GetString(2)].Direction.ToString().ToLowerInvariant(), r.GetInt64(3) != 0), allArgs);
        return new GraphView(focus,
            nodes.Where(n => summaries.ContainsKey(n.Key)).Select(n => new GraphNode(n.Key, summaries[n.Key].Title, summaries[n.Key].Status, n.Value)).ToList(),
            edges);
    });

    public EvidenceView? GetEvidence(string id) => With(c => EvidenceViews(c, "WHERE e.id = @id", ("@id", id)).FirstOrDefault());

    public IReadOnlyList<EvidenceView> ListEvidence() => With(c => EvidenceViews(c, ""));

    public Snapshot? GetSnapshot(string hash) => With(c =>
        c.Query("SELECT hash, media_type, content, created_at FROM snapshots WHERE hash = @h",
            r => new Snapshot(r.GetString(0), r.GetString(1), r.GetString(2), Sql.ParseIso(r.GetString(3))), ("@h", hash)).FirstOrDefault());

    public IReadOnlyList<VisitView> Journey(int limit = 100) => With(c =>
        c.Query("SELECT id, at, concept_id, from_id, direction FROM learner_visits ORDER BY id DESC LIMIT @l",
            r => new VisitView(r.GetInt64(0), Sql.ParseIso(r.GetString(1)), r.GetString(2), r.NullableString(3), r.NullableString(4)),
            ("@l", limit)).AsEnumerable().Reverse().ToList());

    public IReadOnlyList<string> Known() => With(c => c.Query("SELECT concept_id FROM learner_known ORDER BY concept_id", r => r.GetString(0)));

    // ---------- helpers ----------

    internal static string StatusOf(bool hasFacts, bool hasTeaching) => (hasFacts, hasTeaching) switch
    {
        (false, false) => "stub",
        (false, true) => "unverified",
        (true, false) => "researched",
        (true, true) => "complete",
    };

    private static List<ConceptSummary> AllSummaries(SqliteConnection c) =>
        c.Query("""
            SELECT c.id, c.title, c.gloss, c.summary IS NOT NULL, EXISTS (SELECT 1 FROM facts f WHERE f.concept_id = c.id)
            FROM concepts c ORDER BY c.id
            """, r => new ConceptSummary(r.GetString(0), r.GetString(1), r.GetString(2), StatusOf(r.GetInt64(4) != 0, r.GetInt64(3) != 0)));

    private static List<NeighborView> NeighborsOf(SqliteConnection c, string id)
    {
        var summaries = AllSummaries(c).ToDictionary(s => s.Id);
        var evidence = c.Query("SELECT source, target, evidence_id FROM edge_evidence WHERE source = @id OR target = @id",
            r => (r.GetString(0), r.GetString(1), r.GetString(2)), ("@id", id)).ToLookup(x => (x.Item1, x.Item2), x => x.Item3);
        return c.Query("SELECT source, target, type, legacy FROM edges WHERE source = @id OR target = @id ORDER BY type, source, target",
            r => (Source: r.GetString(0), Target: r.GetString(1), Type: r.GetString(2), Legacy: r.GetInt64(3) != 0), ("@id", id))
            .Select(e =>
            {
                var type = EdgeTypes.All[e.Type];
                var outgoing = e.Source == id;
                var other = summaries[outgoing ? e.Target : e.Source];
                var dir = outgoing ? type.Direction : EdgeTypes.Invert(type.Direction);
                return new NeighborView(other.Id, other.Title, other.Gloss, other.Status, dir.ToString().ToLowerInvariant(),
                    outgoing ? type.Name : type.InverseLabel, type.Name, e.Legacy, evidence[(e.Source, e.Target)].ToList());
            }).ToList();
    }

    private static List<EvidenceView> EvidenceViews(SqliteConnection c, string where, params (string, object?)[] args) =>
        c.Query($"""
            SELECT e.id, e.source, s.system, e.locator, e.quote, e.trust, e.stale, s.min_trust,
                   e.captured_by, e.reconfirmed_by, e.retrieved_at, e.snapshot_hash
            FROM evidence e JOIN sources s ON s.name = e.source
            {where} ORDER BY e.id
            """, r => new EvidenceView(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6) != 0,
                TrustNames.Parse(r.GetString(5)) >= TrustNames.Parse(r.GetString(7)),
                r.GetString(8), r.NullableString(9), Sql.ParseIso(r.GetString(10)), r.GetString(11)), args);

    internal static List<QueueMessage> OpenWork(SqliteConnection c, string conceptId) =>
        c.Query($"{SqliteWorkQueue.Select} WHERE state IN ('ready', 'leased') AND json_extract(payload, '$.conceptId') = @id ORDER BY id",
            SqliteWorkQueue.Map, ("@id", conceptId));
}

internal sealed class SqliteEventLog(string path) : IEventLog
{
    public IReadOnlyList<StoredEvent> Read(long after, IReadOnlyCollection<string>? types = null, int limit = 100)
    {
        using var c = Sql.Open(path);
        var filter = "";
        (string, object?)[] args = [("@after", after), ("@lim", limit)];
        if (types is { Count: > 0 })
        {
            var (clause, typeArgs) = Sql.In("t", types);
            filter = $"AND type IN {clause}";
            args = [.. args, .. typeArgs];
        }
        return c.Query($"SELECT seq, type, stream, at, payload FROM events WHERE seq > @after {filter} ORDER BY seq LIMIT @lim",
            r => new StoredEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), Sql.ParseIso(r.GetString(3)), r.GetString(4)), args);
    }

    public long LatestSeq()
    {
        using var c = Sql.Open(path);
        return Convert.ToInt64(c.Scalar("SELECT coalesce(max(seq), 0) FROM events"));
    }
}
