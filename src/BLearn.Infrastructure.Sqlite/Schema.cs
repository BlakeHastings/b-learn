using BLearn.Domain;
using Microsoft.Data.Sqlite;

namespace BLearn.Infrastructure.Sqlite;

/// <summary>
/// The store schema. Constraints here are a backstop for the domain invariants: if domain code has a
/// bug, the database still refuses obviously bad data.
/// </summary>
internal static class Schema
{
    public const int Version = 1;

    private static string EdgeTypeList => string.Join(", ", EdgeTypes.List.Select(t => $"'{t.Name}'"));

    private static string Ddl => $"""
        CREATE TABLE meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE sources (
            name      TEXT PRIMARY KEY,
            system    TEXT NOT NULL,
            scope     TEXT NOT NULL CHECK (length(scope) > 0),
            min_trust TEXT NOT NULL CHECK (min_trust IN ('captured', 'reconfirmed', 'fetched'))
        );

        CREATE TABLE concepts (
            id               TEXT PRIMARY KEY
                             CHECK (id GLOB '[a-z0-9]*' AND id NOT GLOB '*[^a-z0-9-]*' AND id NOT GLOB '*--*' AND id NOT GLOB '*-'),
            title            TEXT NOT NULL CHECK (length(title) > 0),
            gloss            TEXT NOT NULL CHECK (length(gloss) BETWEEN 1 AND {Gloss.MaxLength} AND instr(gloss, char(10)) = 0),
            summary          TEXT,
            explanation      TEXT,
            teaching_legacy  INTEGER NOT NULL DEFAULT 0,
            version          INTEGER NOT NULL
        );

        -- Every name a concept answers to (id, title, aliases), normalized. UNIQUE stops duplicates.
        CREATE TABLE concept_names (
            normalized  TEXT PRIMARY KEY,
            concept_id  TEXT NOT NULL REFERENCES concepts(id) ON DELETE CASCADE,
            name        TEXT NOT NULL,
            kind        TEXT NOT NULL CHECK (kind IN ('id', 'title', 'alias'))
        );
        CREATE INDEX concept_names_concept ON concept_names(concept_id);

        CREATE TABLE snapshots (
            hash        TEXT PRIMARY KEY,
            media_type  TEXT NOT NULL,
            content     TEXT NOT NULL,
            created_at  TEXT NOT NULL
        );

        CREATE TABLE evidence (
            id              TEXT PRIMARY KEY,
            source          TEXT NOT NULL REFERENCES sources(name),
            locator         TEXT NOT NULL CHECK (length(locator) > 0),
            quote           TEXT NOT NULL CHECK (length(quote) BETWEEN 1 AND {Evidence.QuoteMaxLength}),
            snapshot_hash   TEXT NOT NULL REFERENCES snapshots(hash),
            trust           TEXT NOT NULL CHECK (trust IN ('captured', 'reconfirmed', 'fetched')),
            stale           INTEGER NOT NULL DEFAULT 0,
            captured_by     TEXT NOT NULL,
            reconfirmed_by  TEXT,
            retrieved_at    TEXT NOT NULL,
            version         INTEGER NOT NULL
        );

        CREATE TABLE facts (
            concept_id  TEXT NOT NULL REFERENCES concepts(id) ON DELETE CASCADE,
            ordinal     INTEGER NOT NULL,
            text        TEXT NOT NULL CHECK (length(text) > 0),
            PRIMARY KEY (concept_id, ordinal)
        );

        CREATE TABLE fact_evidence (
            concept_id   TEXT NOT NULL,
            ordinal      INTEGER NOT NULL,
            evidence_id  TEXT NOT NULL REFERENCES evidence(id),
            PRIMARY KEY (concept_id, ordinal, evidence_id),
            FOREIGN KEY (concept_id, ordinal) REFERENCES facts(concept_id, ordinal) ON DELETE CASCADE
        );

        CREATE TABLE edges (
            source  TEXT NOT NULL REFERENCES concepts(id),
            target  TEXT NOT NULL REFERENCES concepts(id),
            type    TEXT NOT NULL CHECK (type IN ({EdgeTypeList})),
            legacy  INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (source, target),
            CHECK (source <> target)
        );
        -- One relationship per pair, whichever side stores it.
        CREATE UNIQUE INDEX edges_pair ON edges(min(source, target), max(source, target));
        CREATE INDEX edges_target ON edges(target);

        CREATE TABLE edge_evidence (
            source       TEXT NOT NULL,
            target       TEXT NOT NULL,
            evidence_id  TEXT NOT NULL REFERENCES evidence(id),
            PRIMARY KEY (source, target, evidence_id),
            FOREIGN KEY (source, target) REFERENCES edges(source, target) ON DELETE CASCADE
        );

        CREATE TABLE events (
            seq      INTEGER PRIMARY KEY AUTOINCREMENT,
            type     TEXT NOT NULL,
            stream   TEXT NOT NULL,
            at       TEXT NOT NULL,
            payload  TEXT NOT NULL
        );
        CREATE INDEX events_type ON events(type, seq);

        CREATE TABLE messages (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            queue        TEXT NOT NULL,
            dedup_key    TEXT,
            payload      TEXT NOT NULL,
            priority     INTEGER NOT NULL DEFAULT 0,
            state        TEXT NOT NULL CHECK (state IN ('ready', 'leased', 'done', 'dead')),
            attempts     INTEGER NOT NULL DEFAULT 0,
            created_at   INTEGER NOT NULL,
            lease_until  INTEGER,
            receipt      TEXT UNIQUE,
            received_by  TEXT,
            last_error   TEXT
        );
        CREATE INDEX messages_ready ON messages(state, queue, priority, id);
        -- While a message with a key is waiting or leased, the same key cannot be queued again.
        CREATE UNIQUE INDEX messages_dedup ON messages(queue, dedup_key)
            WHERE dedup_key IS NOT NULL AND state IN ('ready', 'leased');

        CREATE TABLE learner_visits (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            concept_id   TEXT NOT NULL REFERENCES concepts(id),
            from_id      TEXT REFERENCES concepts(id),
            direction    TEXT CHECK (direction IN ('down', 'up', 'lateral')),
            at           TEXT NOT NULL
        );

        CREATE TABLE learner_known (
            concept_id  TEXT PRIMARY KEY REFERENCES concepts(id),
            at          TEXT NOT NULL
        );

        CREATE TABLE learner_recall (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            concept_id  TEXT NOT NULL REFERENCES concepts(id),
            answer      TEXT NOT NULL,
            feedback    TEXT,
            at          TEXT NOT NULL
        );
        """;

    public static void Create(SqliteConnection c)
    {
        using var tx = c.BeginTransaction();
        Exec(c, Ddl, tx);
        Exec(c, $"INSERT INTO meta(key, value) VALUES ('schema_version', '{Version}'), ('settings_version', '0')", tx);
        tx.Commit();
    }

    public static void Check(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
        object? v;
        try { v = cmd.ExecuteScalar(); }
        catch (SqliteException) { throw new InvalidDataException("Not a b-learn store (no meta table)."); }
        if (v is not string s || s != Version.ToString())
            throw new InvalidDataException($"Unsupported store schema version '{v}'. Expected {Version}.");
    }

    public static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
