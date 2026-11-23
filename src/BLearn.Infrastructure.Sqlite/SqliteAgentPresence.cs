using System.Text.Json;
using BLearn.Application;
using Microsoft.Data.Sqlite;

namespace BLearn.Infrastructure.Sqlite;

internal sealed class SqliteAgentPresence(string path, IClock clock) : IAgentPresence
{
    public void Heartbeat(string session, string? harness = null, bool? managed = null, string? activity = null)
    {
        using var c = Sql.Open(path);
        Upsert(c, session, Sql.UnixMs(clock.Now), harness, managed, activity, listening: null);
    }

    public void Listening(string session, IReadOnlyCollection<string> queues)
    {
        using var c = Sql.Open(path);
        Upsert(c, session, Sql.UnixMs(clock.Now), null, null, null, JsonSerializer.Serialize(queues));
    }

    public void Leave(string session)
    {
        using var c = Sql.Open(path);
        c.Exec("UPDATE agents SET left_at = @now, listening_at = NULL WHERE session = @s", ("@now", Sql.UnixMs(clock.Now)), ("@s", session));
    }

    /// <summary>Shared with the work queue so a receive and its heartbeat use one connection.</summary>
    internal static void Upsert(SqliteConnection c, string session, long now, string? harness, bool? managed, string? activity, string? listening)
    {
        c.Exec("""
            INSERT INTO agents(session, harness, managed, last_seen, listening_at, queues, activity, left_at)
            VALUES (@s, @h, coalesce(@m, 0), @now, CASE WHEN @q IS NULL THEN NULL ELSE @now END, @q, @a, NULL)
            ON CONFLICT(session) DO UPDATE SET
                last_seen = @now,
                left_at = NULL,
                harness = coalesce(@h, harness),
                managed = coalesce(@m, managed),
                activity = CASE WHEN @q IS NOT NULL THEN NULL ELSE coalesce(@a, activity) END,
                listening_at = CASE WHEN @q IS NOT NULL THEN @now ELSE listening_at END,
                queues = coalesce(@q, queues)
            """,
            ("@s", session), ("@h", harness), ("@m", managed is { } m ? (m ? 1 : 0) : null), ("@now", now), ("@q", listening), ("@a", activity));
    }

    public AgentsStatus Status()
    {
        using var c = Sql.Open(path);
        var now = clock.Now;
        var nowMs = Sql.UnixMs(now);
        var work = c.Query("""
            SELECT received_by, id, queue, payload FROM messages
            WHERE state = 'leased' AND lease_until > @now AND received_by IS NOT NULL
            ORDER BY id
            """, r => (Session: r.GetString(0), Work: ToWork(r.GetInt64(1), r.GetString(2), r.GetString(3))), ("@now", nowMs))
            .GroupBy(x => x.Session).ToDictionary(g => g.Key, g => g.First().Work);

        var since = nowMs - (long)TimeSpan.FromDays(1).TotalMilliseconds;
        var agents = c.Query("""
            SELECT session, harness, managed, last_seen, listening_at, activity, left_at FROM agents
            WHERE last_seen > @since ORDER BY last_seen DESC
            """, r =>
            {
                var session = r.GetString(0);
                var lastSeen = Sql.FromUnixMs(r.GetInt64(3));
                var listeningAt = r.NullableLong(4) is { } l ? Sql.FromUnixMs(l) : (DateTimeOffset?)null;
                var left = !r.IsDBNull(6);
                work.TryGetValue(session, out var w);
                var state = left ? AgentStates.Offline
                    : w is not null && now - lastSeen < AgentStates.WorkingWindow ? AgentStates.Working
                    : listeningAt is { } la && now - la < AgentStates.ListeningWindow ? AgentStates.Listening
                    : AgentStates.Offline;
                return new AgentView(session, r.NullableString(1), r.GetInt64(2) != 0, state, lastSeen,
                    state == AgentStates.Offline ? null : r.NullableString(5), state == AgentStates.Working ? w : null);
            }, ("@since", since));
        return new AgentsStatus(agents.Any(a => a.State != AgentStates.Offline), agents);
    }

    private static AgentWork ToWork(long id, string queue, string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new AgentWork(id, queue, Str("conceptId"), Str("text"));
    }
}
