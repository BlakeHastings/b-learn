using Microsoft.Data.Sqlite;

namespace BLearn.Infrastructure.Sqlite;

/// <summary>Small helpers over raw ADO.NET, so SQL stays readable at the call site.</summary>
internal static class Sql
{
    public static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        c.Open();
        Pragmas(c);
        return c;
    }

    public static void Pragmas(SqliteConnection c) =>
        Schema.Exec(c, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");

    public static SqliteCommand Cmd(this SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static int Exec(this SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.Cmd(sql, args);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(this SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.Cmd(sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public static List<T> Query<T>(this SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using var cmd = c.Cmd(sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public static string? NullableString(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long? NullableLong(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    public static string Iso(DateTimeOffset t) => t.ToString("o");
    public static DateTimeOffset ParseIso(string s) => DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
    public static long UnixMs(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    public static DateTimeOffset FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();

    /// <summary>Builds "(@p0, @p1, ...)" and the matching parameters for an IN clause.</summary>
    public static (string Clause, (string, object?)[] Args) In(string prefix, IEnumerable<string> values)
    {
        var list = values.ToList();
        var names = list.Select((_, i) => $"@{prefix}{i}").ToList();
        return ($"({string.Join(", ", names)})", list.Select((v, i) => (names[i], (object?)v)).ToArray());
    }
}
