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
                    WHERE (state = 'ready' OR (state = 'leased' AND lease_until < @now)) {queueFilter}
                    ORDER BY priority DESC, id LIMIT 1
                    """, r => (Id: r.GetInt64(0), Attempts: (int)r.GetInt64(1)), args).FirstOrDefault();
                if (candidate.Id == 0)
                {
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

    public bool Ack(string receipt)
    {
        using var c = Sql.Open(path);
        return c.Exec("UPDATE messages SET state = 'done', lease_until = NULL WHERE receipt = @r AND state = 'leased'", ("@r", receipt)) == 1;
    }

    public bool Nack(string receipt, string? error)
    {
        using var c = Sql.Open(path);
        return c.Exec($"""
            UPDATE messages
            SET state = CASE WHEN attempts >= {MaxAttempts} THEN 'dead' ELSE 'ready' END,
                lease_until = NULL, receipt = NULL, last_error = @e
            WHERE receipt = @r AND state = 'leased'
            """, ("@r", receipt), ("@e", error)) == 1;
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
