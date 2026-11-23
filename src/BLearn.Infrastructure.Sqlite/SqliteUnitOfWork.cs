using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BLearn.Application;
using BLearn.Domain;
using Microsoft.Data.Sqlite;

namespace BLearn.Infrastructure.Sqlite;

/// <summary>
/// One write transaction. It starts with BEGIN IMMEDIATE, taking SQLite's single write lock up front,
/// so invariant checks and writes see a consistent store and concurrent commands are serialized.
/// Repositories apply changes as they are saved; events and queued work are appended on commit.
/// </summary>
internal sealed class SqliteUnitOfWork : IUnitOfWork
{
    private readonly SqliteConnection _c;
    private readonly IClock _clock;
    private readonly List<DomainEvent> _pending = [];
    private readonly List<WorkItem> _work = [];
    private bool _done;

    public SqliteUnitOfWork(SqliteConnection connection, IClock clock)
    {
        _c = connection;
        _clock = clock;
        _c.Exec("BEGIN IMMEDIATE");
        Reader = new SqliteGraphReader(_c);
        Concepts = new ConceptRepository(this);
        Evidence = new EvidenceRepository(this);
        Settings = new SettingsRepository(this);
        Graph = new GraphRepository(this);
        Learner = new LearnerRepository(this);
        Conversations = new ConversationRepository(this);
        Snapshots = new SnapshotStore(this);
    }

    internal SqliteConnection Connection => _c;
    internal IClock Clock => _clock;

    public IGraphReader Reader { get; }
    public IConceptRepository Concepts { get; }
    public IEvidenceRepository Evidence { get; }
    public IStoreSettingsRepository Settings { get; }
    public IGraphRepository Graph { get; }
    public ILearnerRepository Learner { get; }
    public IConversationRepository Conversations { get; }
    public ISnapshotStore Snapshots { get; }
    public IReadOnlyList<DomainEvent> PendingEvents => _pending;

    internal void Track(AggregateRoot a) => _pending.AddRange(a.PendingEvents);

    public void Enqueue(WorkItem item) => _work.Add(item);

    public IReadOnlyList<StoredEvent> Commit()
    {
        var at = _clock.Now;
        var stored = new List<StoredEvent>();
        foreach (var e in _pending)
        {
            var payload = JsonSerializer.Serialize(e, e.GetType(), Json.Options);
            var seq = (long)_c.Scalar(
                "INSERT INTO events(type, stream, at, payload) VALUES (@t, @s, @at, @p) RETURNING seq",
                ("@t", e.Type), ("@s", e.Stream), ("@at", Sql.Iso(at)), ("@p", payload))!;
            stored.Add(new StoredEvent(seq, e.Type, e.Stream, at, payload));
        }
        foreach (var w in _work)
        {
            _c.Exec("""
                INSERT OR IGNORE INTO messages(queue, dedup_key, payload, priority, state, created_at)
                VALUES (@q, @k, @p, @pr, 'ready', @at)
                """,
                ("@q", w.Queue), ("@k", w.DedupKey), ("@p", w.PayloadJson), ("@pr", w.Priority), ("@at", Sql.UnixMs(at)));
        }
        _c.Exec("COMMIT");
        _done = true;
        return stored;
    }

    public void Dispose()
    {
        if (!_done)
        {
            try { _c.Exec("ROLLBACK"); } catch (SqliteException) { }
        }
        _c.Dispose();
    }

    /// <summary>Translates constraint failures into domain violations, so a backstop hit reads like a rule.</summary>
    internal static T Guard<T>(Func<T> act)
    {
        try { return act(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            throw new DomainViolation("storage-constraint", $"The store rejected the change: {ex.Message}");
        }
    }
}

internal sealed class SqliteGraphReader(SqliteConnection c) : IGraphReader
{
    public bool ConceptExists(string id) => c.Scalar("SELECT 1 FROM concepts WHERE id = @id", ("@id", id)) is not null;

    public string? NameOwner(string normalizedName) =>
        c.Scalar("SELECT concept_id FROM concept_names WHERE normalized = @n", ("@n", normalizedName)) as string;

    public EdgeRecord? EdgeBetween(string a, string b) =>
        c.Query("""
            SELECT source, type, target FROM edges
            WHERE (source = @a AND target = @b) OR (source = @b AND target = @a)
            """, r => new EdgeRecord(r.GetString(0), r.GetString(1), r.GetString(2)), ("@a", a), ("@b", b))
        .FirstOrDefault();

    public bool DownPathExists(string from, string to)
    {
        var (types, args) = Sql.In("t", EdgeTypes.DownNames);
        return c.Scalar($"""
            WITH RECURSIVE down(id) AS (
                SELECT @from
                UNION
                SELECT e.target FROM edges e JOIN down d ON e.source = d.id WHERE e.type IN {types}
            )
            SELECT 1 FROM down WHERE id = @to LIMIT 1
            """, [("@from", from), ("@to", to), .. args]) is not null;
    }

    public bool EvidenceExists(string id) => c.Scalar("SELECT 1 FROM evidence WHERE id = @id", ("@id", id)) is not null;

    public string? EvidenceCapturedBy(string id) =>
        c.Scalar("SELECT captured_by FROM evidence WHERE id = @id", ("@id", id)) as string;

    public bool EvidenceCited(string id) => c.Scalar("""
        SELECT 1 FROM fact_evidence WHERE evidence_id = @id
        UNION ALL SELECT 1 FROM edge_evidence WHERE evidence_id = @id
        LIMIT 1
        """, ("@id", id)) is not null;
}

internal sealed class ConceptRepository(SqliteUnitOfWork uow) : IConceptRepository
{
    private SqliteConnection C => uow.Connection;

    public Concept? Find(string id)
    {
        var row = C.Query("SELECT id, title, gloss, summary, explanation, teaching_legacy, version FROM concepts WHERE id = @id",
            r => (Id: r.GetString(0), Title: r.GetString(1), Gloss: r.GetString(2), Summary: r.NullableString(3),
                Explanation: r.NullableString(4), Legacy: r.GetInt64(5) != 0, Version: r.GetInt64(6)), ("@id", id)).FirstOrDefault();
        if (row.Id is null) return null;

        var aliases = C.Query("SELECT name FROM concept_names WHERE concept_id = @id AND kind = 'alias' ORDER BY name",
            r => r.GetString(0), ("@id", id));
        var evidence = C.Query("SELECT ordinal, evidence_id FROM fact_evidence WHERE concept_id = @id ORDER BY evidence_id",
            r => (Ordinal: r.GetInt64(0), Evidence: r.GetString(1)), ("@id", id)).ToLookup(x => x.Ordinal, x => x.Evidence);
        var facts = C.Query("SELECT ordinal, text FROM facts WHERE concept_id = @id ORDER BY ordinal",
            r => new Fact(r.GetString(1), evidence[r.GetInt64(0)].ToList()), ("@id", id));

        var recall = C.Query("SELECT prompt, answer, facts FROM recall_questions WHERE concept_id = @id ORDER BY ordinal",
            r => new RecallQuestion(r.GetString(0), r.GetString(1), JsonSerializer.Deserialize<List<int>>(r.GetString(2))!), ("@id", id));
        CheckRecord? check = null;
        var checkRow = C.Query("SELECT session, at FROM concept_checks WHERE concept_id = @id",
            r => (Session: r.GetString(0), At: Sql.ParseIso(r.GetString(1))), ("@id", id)).ToList();
        if (checkRow.Count == 1)
        {
            var verdicts = C.Query("SELECT ordinal, verdict, note FROM fact_checks WHERE concept_id = @id ORDER BY ordinal",
                r => new FactCheck((int)r.GetInt64(0), Enum.Parse<Verdict>(r.GetString(1), ignoreCase: true), r.NullableString(2)), ("@id", id));
            var flags = C.Query("SELECT excerpt, note FROM teaching_flags WHERE concept_id = @id ORDER BY ordinal",
                r => new TeachingFlag(r.GetString(0), r.GetString(1)), ("@id", id));
            check = new CheckRecord(verdicts, flags, checkRow[0].Session, checkRow[0].At);
        }

        return Concept.Rehydrate(row.Id, row.Title, row.Gloss, aliases, facts, row.Summary, row.Explanation, row.Legacy, row.Version, recall, check);
    }

    public void Save(Concept concept)
    {
        if (concept.PendingEvents.Count == 0) return;
        SqliteUnitOfWork.Guard(() =>
        {
            if (concept.Version == 0)
            {
                C.Exec("""
                    INSERT INTO concepts(id, title, gloss, summary, explanation, teaching_legacy, version)
                    VALUES (@id, @t, @g, @s, @e, @l, 1)
                    """, Params(concept));
            }
            else
            {
                var rows = C.Exec("""
                    UPDATE concepts SET title = @t, gloss = @g, summary = @s, explanation = @e, teaching_legacy = @l,
                        version = version + 1
                    WHERE id = @id AND version = @v
                    """, [.. Params(concept), ("@v", concept.Version)]);
                if (rows == 0) throw new ConcurrencyConflict($"Concept '{concept.Id}' changed since it was read. Reload and retry.");
            }

            C.Exec("DELETE FROM concept_names WHERE concept_id = @id", ("@id", concept.Id));
            var seen = new HashSet<string>();
            foreach (var (name, kind) in concept.Aliases.Select(a => (a, "alias")).Prepend((concept.Title, "title")).Prepend((concept.Id, "id")))
            {
                var key = Names.Normalize(name);
                if (!seen.Add(key)) continue;
                C.Exec("INSERT INTO concept_names(normalized, concept_id, name, kind) VALUES (@n, @id, @name, @k)",
                    ("@n", key), ("@id", concept.Id), ("@name", name), ("@k", kind));
            }

            C.Exec("DELETE FROM facts WHERE concept_id = @id", ("@id", concept.Id));
            for (var i = 0; i < concept.Facts.Count; i++)
            {
                C.Exec("INSERT INTO facts(concept_id, ordinal, text) VALUES (@id, @o, @t)",
                    ("@id", concept.Id), ("@o", i), ("@t", concept.Facts[i].Text));
                foreach (var e in concept.Facts[i].EvidenceIds)
                    C.Exec("INSERT INTO fact_evidence(concept_id, ordinal, evidence_id) VALUES (@id, @o, @e)",
                        ("@id", concept.Id), ("@o", i), ("@e", e));
            }

            C.Exec("DELETE FROM recall_questions WHERE concept_id = @id", ("@id", concept.Id));
            for (var i = 0; i < concept.RecallQuestions.Count; i++)
            {
                var q = concept.RecallQuestions[i];
                C.Exec("INSERT INTO recall_questions(concept_id, ordinal, prompt, answer, facts) VALUES (@id, @o, @p, @a, @f)",
                    ("@id", concept.Id), ("@o", i), ("@p", q.Prompt), ("@a", q.Answer), ("@f", JsonSerializer.Serialize(q.FactIndexes)));
            }

            // fact_checks rows went with the facts (ON DELETE CASCADE); concept_checks cascades to teaching_flags.
            C.Exec("DELETE FROM concept_checks WHERE concept_id = @id", ("@id", concept.Id));
            if (concept.Check is { } check)
            {
                C.Exec("INSERT INTO concept_checks(concept_id, session, at) VALUES (@id, @s, @at)",
                    ("@id", concept.Id), ("@s", check.Session), ("@at", Sql.Iso(check.At)));
                foreach (var v in check.Facts)
                    C.Exec("INSERT INTO fact_checks(concept_id, ordinal, verdict, note) VALUES (@id, @o, @v, @n)",
                        ("@id", concept.Id), ("@o", v.FactIndex), ("@v", v.Verdict.ToString().ToLowerInvariant()), ("@n", v.Note));
                for (var i = 0; i < check.Flags.Count; i++)
                    C.Exec("INSERT INTO teaching_flags(concept_id, ordinal, excerpt, note) VALUES (@id, @o, @e, @n)",
                        ("@id", concept.Id), ("@o", i), ("@e", check.Flags[i].Excerpt), ("@n", check.Flags[i].Note));
            }
            return 0;
        });
        uow.Track(concept);
        concept.MarkPersisted(concept.Version + 1);
    }

    private static (string, object?)[] Params(Concept c) =>
    [
        ("@id", c.Id), ("@t", c.Title), ("@g", c.Gloss), ("@s", c.Summary), ("@e", c.Explanation), ("@l", c.TeachingLegacy ? 1 : 0),
    ];
}

internal sealed class EvidenceRepository(SqliteUnitOfWork uow) : IEvidenceRepository
{
    private SqliteConnection C => uow.Connection;

    public Evidence? Find(string id) =>
        C.Query("""
            SELECT id, source, locator, quote, snapshot_hash, trust, stale, captured_by, reconfirmed_by, retrieved_at, version
            FROM evidence WHERE id = @id
            """,
            r => Domain.Evidence.Rehydrate(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                TrustNames.Parse(r.GetString(5)), r.GetInt64(6) != 0, r.GetString(7), r.NullableString(8),
                Sql.ParseIso(r.GetString(9)), r.GetInt64(10)),
            ("@id", id)).FirstOrDefault();

    public void Save(Evidence e)
    {
        if (e.PendingEvents.Count == 0) return;
        SqliteUnitOfWork.Guard(() =>
        {
            (string, object?)[] p =
            [
                ("@id", e.Id), ("@src", e.Source), ("@loc", e.Locator), ("@q", e.Quote), ("@h", e.SnapshotHash),
                ("@t", e.Trust.ToName()), ("@st", e.Stale ? 1 : 0), ("@cb", e.CapturedBy), ("@rb", e.ReconfirmedBy),
                ("@ra", Sql.Iso(e.RetrievedAt)), ("@v", e.Version),
            ];
            if (e.Removed)
            {
                // The foreign keys from fact_evidence and edge_evidence still refuse this if a citation slipped through.
                if (C.Exec("DELETE FROM evidence WHERE id = @id AND version = @v", p) == 0)
                    throw new ConcurrencyConflict($"Evidence '{e.Id}' changed since it was read. Reload and retry.");
            }
            else if (e.Version == 0)
            {
                C.Exec("""
                    INSERT INTO evidence(id, source, locator, quote, snapshot_hash, trust, stale, captured_by, reconfirmed_by, retrieved_at, version)
                    VALUES (@id, @src, @loc, @q, @h, @t, @st, @cb, @rb, @ra, 1)
                    """, p);
            }
            else if (C.Exec("""
                UPDATE evidence SET snapshot_hash = @h, trust = @t, stale = @st, reconfirmed_by = @rb, version = version + 1
                WHERE id = @id AND version = @v
                """, p) == 0)
            {
                throw new ConcurrencyConflict($"Evidence '{e.Id}' changed since it was read. Reload and retry.");
            }
            return 0;
        });
        uow.Track(e);
        e.MarkPersisted(e.Version + 1);
    }
}

internal sealed class SettingsRepository(SqliteUnitOfWork uow) : IStoreSettingsRepository
{
    private SqliteConnection C => uow.Connection;

    public StoreSettings Load()
    {
        var meta = C.Query("SELECT key, value FROM meta", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
        var sources = C.Query("SELECT name, system, scope, min_trust FROM sources ORDER BY name",
            r => new SourceOfTruth(r.GetString(0), r.GetString(1), r.GetString(2), TrustNames.Parse(r.GetString(3))));
        if (!meta.TryGetValue("name", out var name))
            throw new DomainViolation("store-uninitialized", "The store has not been initialized.");
        return StoreSettings.Rehydrate(name, meta["root"], meta["private"] == "1", sources, long.Parse(meta["settings_version"]));
    }

    public void Save(StoreSettings s)
    {
        if (s.PendingEvents.Count == 0) return;
        SqliteUnitOfWork.Guard(() =>
        {
            var current = long.Parse((string)C.Scalar("SELECT value FROM meta WHERE key = 'settings_version'")!);
            if (current != s.Version) throw new ConcurrencyConflict("Store settings changed since they were read. Reload and retry.");
            foreach (var (k, v) in new[] { ("name", s.Name), ("root", s.Root), ("private", s.Private ? "1" : "0"), ("settings_version", (s.Version + 1).ToString()) })
                C.Exec("INSERT INTO meta(key, value) VALUES (@k, @v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("@k", k), ("@v", v));
            C.Exec("DELETE FROM sources");
            foreach (var src in s.Sources)
                C.Exec("INSERT INTO sources(name, system, scope, min_trust) VALUES (@n, @s, @sc, @m)",
                    ("@n", src.Name), ("@s", src.System), ("@sc", src.Scope), ("@m", src.MinTrust.ToName()));
            return 0;
        });
        uow.Track(s);
        s.MarkPersisted(s.Version + 1);
    }
}

/// <summary>Edges are persisted by applying the graph's events; the write lock serializes graph changes.</summary>
internal sealed class GraphRepository(SqliteUnitOfWork uow) : IGraphRepository
{
    private SqliteConnection C => uow.Connection;

    public Graph Load() => new(uow.Reader);

    public void Save(Graph graph)
    {
        if (graph.PendingEvents.Count == 0) return;
        SqliteUnitOfWork.Guard(() =>
        {
            foreach (var e in graph.PendingEvents)
            {
                switch (e)
                {
                    case EdgeAdded a:
                        C.Exec("INSERT INTO edges(source, target, type, legacy) VALUES (@s, @t, @ty, @l)",
                            ("@s", a.Source), ("@t", a.Target), ("@ty", a.EdgeType), ("@l", a.Legacy ? 1 : 0));
                        foreach (var ev in a.EvidenceIds)
                            C.Exec("INSERT INTO edge_evidence(source, target, evidence_id) VALUES (@s, @t, @e)", ("@s", a.Source), ("@t", a.Target), ("@e", ev));
                        break;
                    case EdgeRemoved r:
                        C.Exec("DELETE FROM edges WHERE source = @s AND target = @t", ("@s", r.Source), ("@t", r.Target));
                        break;
                    case EdgeEvidenceAttached x:
                        foreach (var ev in x.EvidenceIds)
                            C.Exec("INSERT OR IGNORE INTO edge_evidence(source, target, evidence_id) VALUES (@s, @t, @e)", ("@s", x.Source), ("@t", x.Target), ("@e", ev));
                        C.Exec("UPDATE edges SET legacy = 0 WHERE source = @s AND target = @t", ("@s", x.Source), ("@t", x.Target));
                        break;
                }
            }
            return 0;
        });
        uow.Track(graph);
        graph.MarkPersisted(0);
    }
}

internal sealed class LearnerRepository(SqliteUnitOfWork uow) : ILearnerRepository
{
    private SqliteConnection C => uow.Connection;

    public Learner Load() => new(uow.Reader);

    public void Save(Learner learner)
    {
        if (learner.PendingEvents.Count == 0) return;
        var at = Sql.Iso(uow.Clock.Now);
        SqliteUnitOfWork.Guard(() =>
        {
            foreach (var e in learner.PendingEvents)
            {
                switch (e)
                {
                    case ConceptVisited v:
                        C.Exec("INSERT INTO learner_visits(concept_id, from_id, direction, at) VALUES (@c, @f, @d, @at)",
                            ("@c", v.ConceptId), ("@f", v.FromConceptId), ("@d", v.Direction), ("@at", at));
                        break;
                    case ConceptMarkedKnown k:
                        C.Exec("INSERT OR IGNORE INTO learner_known(concept_id, at) VALUES (@c, @at)", ("@c", k.ConceptId), ("@at", at));
                        break;
                    case ConceptUnmarkedKnown u:
                        C.Exec("DELETE FROM learner_known WHERE concept_id = @c", ("@c", u.ConceptId));
                        break;
                    case RecallAnswered a:
                        C.Exec("INSERT INTO learner_recall(concept_id, answer, feedback, at, question_index, correct) VALUES (@c, @a, @f, @at, @q, @ok)",
                            ("@c", a.ConceptId), ("@a", a.Answer), ("@f", a.Feedback), ("@at", at),
                            ("@q", a.QuestionIndex), ("@ok", a.Correct is { } ok ? (ok ? 1 : 0) : null));
                        break;
                }
            }
            return 0;
        });
        uow.Track(learner);
        learner.MarkPersisted(0);
    }
}

internal sealed class ConversationRepository(SqliteUnitOfWork uow) : IConversationRepository
{
    private SqliteConnection C => uow.Connection;

    public Conversation? Find(string id)
    {
        var row = C.Query("SELECT id, concept_id, quote, fact_index, started_at, version FROM conversations WHERE id = @id",
            r => (Id: r.GetString(0), Concept: r.GetString(1), Quote: r.NullableString(2), Fact: r.NullableLong(3),
                At: Sql.ParseIso(r.GetString(4)), Version: r.GetInt64(5)), ("@id", id)).FirstOrDefault();
        if (row.Id is null) return null;
        var messages = C.Query("SELECT ordinal, role, text, session, at FROM conversation_messages WHERE conversation_id = @id ORDER BY ordinal",
            r => new ConversationMessage((int)r.GetInt64(0), r.GetString(1) == "agent" ? MessageRole.Agent : MessageRole.Learner,
                r.GetString(2), r.NullableString(3), Sql.ParseIso(r.GetString(4))), ("@id", id));
        return Conversation.Rehydrate(row.Id, row.Concept, row.Quote, row.Fact is { } f ? (int)f : null, row.At, messages, row.Version);
    }

    public void Save(Conversation c)
    {
        if (c.PendingEvents.Count == 0) return;
        SqliteUnitOfWork.Guard(() =>
        {
            if (c.Version == 0)
            {
                C.Exec("INSERT INTO conversations(id, concept_id, quote, fact_index, started_at, version) VALUES (@id, @c, @q, @f, @at, 1)",
                    ("@id", c.Id), ("@c", c.ConceptId), ("@q", c.Quote), ("@f", c.FactIndex), ("@at", Sql.Iso(c.StartedAt)));
            }
            else if (C.Exec("UPDATE conversations SET version = version + 1 WHERE id = @id AND version = @v", ("@id", c.Id), ("@v", c.Version)) == 0)
            {
                throw new ConcurrencyConflict($"Conversation '{c.Id}' changed since it was read. Reload and retry.");
            }

            // Messages are append-only: insert the ones not stored yet.
            var stored = (long)C.Scalar("SELECT count(*) FROM conversation_messages WHERE conversation_id = @id", ("@id", c.Id))!;
            foreach (var m in c.Messages.Where(m => m.Ordinal >= stored))
                C.Exec("INSERT INTO conversation_messages(conversation_id, ordinal, role, text, session, at) VALUES (@id, @o, @r, @t, @s, @at)",
                    ("@id", c.Id), ("@o", m.Ordinal), ("@r", m.Role == MessageRole.Agent ? "agent" : "learner"), ("@t", m.Text),
                    ("@s", m.Session), ("@at", Sql.Iso(m.At)));
            return 0;
        });
        uow.Track(c);
        c.MarkPersisted(c.Version + 1);
    }
}

internal sealed class SnapshotStore(SqliteUnitOfWork uow) : ISnapshotStore
{
    public string Put(string content, string mediaType)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        uow.Connection.Exec("INSERT OR IGNORE INTO snapshots(hash, media_type, content, created_at) VALUES (@h, @m, @c, @at)",
            ("@h", hash), ("@m", mediaType), ("@c", content), ("@at", Sql.Iso(uow.Clock.Now)));
        return hash;
    }

    public Snapshot? Get(string hash) =>
        uow.Connection.Query("SELECT hash, media_type, content, created_at FROM snapshots WHERE hash = @h",
            r => new Snapshot(r.GetString(0), r.GetString(1), r.GetString(2), Sql.ParseIso(r.GetString(3))), ("@h", hash)).FirstOrDefault();
}
