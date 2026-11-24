using BLearn.Application;
using Microsoft.Data.Sqlite;

namespace BLearn.Infrastructure.Sqlite;

/// <summary>
/// Competing-consumers queue in the store file. A receive runs inside BEGIN IMMEDIATE, which holds
/// SQLite's single write lock, so two receivers can never claim the same message.
/// </summary>
internal sealed class SqliteWorkQueue(string path, IClock clock) : IWorkQueue
{
    public const int MaxAttempts = 5;

    internal const string Select =
        "SELECT id, queue, dedup_key, payload, state, attempts, created_at, lease_until, receipt, received_by, last_error FROM messages";

    internal static QueueMessage Map(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.NullableString(2), r.GetString(3), r.GetString(4), (int)r.GetInt64(5),
        Sql.FromUnixMs(r.GetInt64(6)), r.NullableLong(7) is { } l ? Sql.FromUnixMs(l) : null,
        r.NullableString(8), r.NullableString(9), r.NullableString(10));

    public QueueMessage? TryReceive(IReadOnlyCollection<string> queues, TimeSpan lease, string receiver)
    {
        using var c = Sql.Open(path);
        c.Exec("BEGIN IMMEDIATE");
        try
        {
            var now = Sql.UnixMs(clock.Now);
            var queueFilter = "";
            (string, object?)[] args = [("@now", now)];
            if (queues.Count > 0)
            {
                var (clause, qArgs) = Sql.In("q", queues);
                queueFilter = $"AND queue IN {clause}";
                args = [.. args, .. qArgs];
            }

            while (true)
            {
                var candidate = c.Query($"""
                    SELECT id, attempts FROM messages
                    WHERE ((state = 'ready' AND (lease_until IS NULL OR lease_until <= @now))
                        OR (state = 'leased' AND lease_until < @now)) {queueFilter}
                    ORDER BY priority DESC, id LIMIT 1
                    """, r => (Id: r.GetInt64(0), Attempts: (int)r.GetInt64(1)), args).FirstOrDefault();
                if (candidate.Id == 0)
                {
                    SqliteAgentPresence.Upsert(c, receiver, now, null, null, null, System.Text.Json.JsonSerializer.Serialize(queues));
                    c.Exec("COMMIT");
                    return null;
                }
                if (candidate.Attempts >= MaxAttempts)
                {
                    c.Exec("UPDATE messages SET state = 'dead', receipt = NULL, lease_until = NULL, last_error = coalesce(last_error, 'lease expired too many times') WHERE id = @id",
                        ("@id", candidate.Id));
                    continue;
                }
                var receipt = $"r-{Guid.NewGuid():N}";
                c.Exec("""
                    UPDATE messages SET state = 'leased', attempts = attempts + 1, lease_until = @until, receipt = @r, received_by = @by
                    WHERE id = @id
                    """, ("@until", now + (long)lease.TotalMilliseconds), ("@r", receipt), ("@by", receiver), ("@id", candidate.Id));
                var msg = c.Query($"{Select} WHERE id = @id", Map, ("@id", candidate.Id)).Single();
                SqliteAgentPresence.Upsert(c, receiver, now, null, null, null, System.Text.Json.JsonSerializer.Serialize(queues));
                c.Exec("COMMIT");
                return msg;
            }
        }
        catch
        {
            try { c.Exec("ROLLBACK"); } catch (SqliteException) { }
            throw;
        }
    }

    public bool Ack(string receipt, string? note = null)
    {
        using var c = Sql.Open(path);
        Touch(c, receipt);
        return c.Exec("UPDATE messages SET state = 'done', lease_until = NULL, last_error = coalesce(@n, last_error) WHERE receipt = @r AND state = 'leased'",
            ("@r", receipt), ("@n", note)) == 1;
    }

    public QueueMessage? Find(string receipt)
    {
        using var c = Sql.Open(path);
        return c.Query($"{Select} WHERE receipt = @r", Map, ("@r", receipt)).FirstOrDefault();
    }

    /// <summary>Finishing or releasing work is a sign of life from the agent holding it.</summary>
    private void Touch(SqliteConnection c, string receipt)
    {
        if (c.Scalar("SELECT received_by FROM messages WHERE receipt = @r", ("@r", receipt)) is string session)
            SqliteAgentPresence.Upsert(c, session, Sql.UnixMs(clock.Now), null, null, null, null);
    }

    /// <summary>A released message waits <see cref="ReleaseBackoff"/> per attempt before redelivery, so the releasing agent does not get it straight back.</summary>
    public static readonly TimeSpan ReleaseBackoff = TimeSpan.FromMinutes(1);

    public bool Nack(string receipt, string? error)
    {
        using var c = Sql.Open(path);
        Touch(c, receipt);
        var now = Sql.UnixMs(clock.Now);
        return c.Exec($"""
            UPDATE messages
            SET state = CASE WHEN attempts >= {MaxAttempts} THEN 'dead' ELSE 'ready' END,
                lease_until = CASE WHEN attempts >= {MaxAttempts} THEN NULL ELSE @now + attempts * @backoff END,
                receipt = NULL, last_error = @e
            WHERE receipt = @r AND state = 'leased'
            """, ("@r", receipt), ("@e", error), ("@now", now), ("@backoff", (long)ReleaseBackoff.TotalMilliseconds)) == 1;
    }

    public IReadOnlyList<QueueMessage> List(bool includeDone = false)
    {
        using var c = Sql.Open(path);
        return c.Query($"{Select} {(includeDone ? "" : "WHERE state IN ('ready', 'leased')")} ORDER BY id", Map);
    }

    public IReadOnlyList<QueueMessage> DeadLetters()
    {
        using var c = Sql.Open(path);
        return c.Query($"{Select} WHERE state = 'dead' ORDER BY id", Map);
    }
}
