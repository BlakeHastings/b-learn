using BLearn.Application;
using Microsoft.Data.Sqlite;

namespace BLearn.Infrastructure.Sqlite;

public sealed class SqliteStoreProvider(IClock clock) : IStoreProvider
{
    public IStore Create(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (File.Exists(path)) throw new IOException($"'{path}' already exists.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            c.Open();
            Sql.Pragmas(c);
            Schema.Create(c);
        }
        return Open(path);
    }

    public IStore Open(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException($"No store at '{path}'.", path);
        using (var c = Sql.Open(path)) Schema.Check(c);
        return new SqliteStore(path, clock);
    }
}

/// <summary>An open store file. Each unit of work and query uses its own short-lived connection.</summary>
public sealed class SqliteStore(string path, IClock clock) : IStore
{
    public string Path { get; } = path;
    public IUnitOfWork Begin() => new SqliteUnitOfWork(Sql.Open(Path), clock);
    public IQueries Queries { get; } = new SqliteQueries(path);
    public IEventLog Events { get; } = new SqliteEventLog(path);
    public IWorkQueue Queue { get; } = new SqliteWorkQueue(path, clock);

    public void Dispose() { }
}
