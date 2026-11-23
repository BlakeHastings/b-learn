using BLearn.Domain;

namespace BLearn.Application;

// ---------- Repositories (write side) ----------

public interface IConceptRepository
{
    Concept? Find(string id);
    void Save(Concept concept);
}

public interface IEvidenceRepository
{
    Evidence? Find(string id);
    void Save(Evidence evidence);
}

public interface IStoreSettingsRepository
{
    StoreSettings Load();
    void Save(StoreSettings settings);
}

public interface IGraphRepository
{
    Graph Load();
    void Save(Graph graph);
}

public interface IConversationRepository
{
    Conversation? Find(string id);
    void Save(Conversation conversation);
}

public interface ILearnerRepository
{
    Learner Load();
    void Save(Learner learner);
}

public sealed record Snapshot(string Hash, string MediaType, string Content, DateTimeOffset CreatedAt);

public interface ISnapshotStore
{
    /// <summary>Stores text content by its SHA-256 hash and returns the hash. Idempotent.</summary>
    string Put(string content, string mediaType);

    Snapshot? Get(string hash);
}

/// <summary>A message to put on the work queue when the unit of work commits.</summary>
/// <param name="Queue">Queue name, for example "expand" or "teach".</param>
/// <param name="DedupKey">While a message with this key is waiting or leased, another is not queued.</param>
public sealed record WorkItem(string Queue, string? DedupKey, string PayloadJson, int Priority = 0);

/// <summary>
/// One transaction against a store. Repositories write through it; <see cref="Commit"/> persists
/// aggregate changes, appends their events, and enqueues work atomically.
/// </summary>
public interface IUnitOfWork : IDisposable
{
    IGraphReader Reader { get; }
    IConceptRepository Concepts { get; }
    IEvidenceRepository Evidence { get; }
    IStoreSettingsRepository Settings { get; }
    IGraphRepository Graph { get; }
    ILearnerRepository Learner { get; }
    IConversationRepository Conversations { get; }
    ISnapshotStore Snapshots { get; }

    /// <summary>Events from aggregates saved so far in this unit of work, in order.</summary>
    IReadOnlyList<DomainEvent> PendingEvents { get; }

    void Enqueue(WorkItem item);

    IReadOnlyList<StoredEvent> Commit();
}

/// <summary>Thrown when an aggregate changed between load and save.</summary>
public sealed class ConcurrencyConflict(string message) : Exception(message);

// ---------- Messaging ----------

public sealed record StoredEvent(long Seq, string Type, string Stream, DateTimeOffset At, string PayloadJson);

public interface IEventLog
{
    IReadOnlyList<StoredEvent> Read(long after, IReadOnlyCollection<string>? types = null, int limit = 100);
    long LatestSeq();
}

public sealed record QueueMessage(
    long Id, string Queue, string? DedupKey, string PayloadJson, string State, int Attempts,
    DateTimeOffset CreatedAt, DateTimeOffset? LeaseUntil, string? Receipt, string? ReceivedBy, string? LastError);

public interface IWorkQueue
{
    /// <summary>
    /// Atomically claims the next available message from any of <paramref name="queues"/> (all queues when empty),
    /// including messages whose lease expired. Returns null when nothing is available.
    /// </summary>
    QueueMessage? TryReceive(IReadOnlyCollection<string> queues, TimeSpan lease, string receiver);

    /// <summary>Marks a leased message done. False when the receipt is unknown or the lease was lost.</summary>
    bool Ack(string receipt);

    /// <summary>Returns a leased message to the queue, or dead-letters it after too many attempts.</summary>
    bool Nack(string receipt, string? error);

    IReadOnlyList<QueueMessage> List(bool includeDone = false);
    IReadOnlyList<QueueMessage> DeadLetters();
}

// ---------- Store lifecycle ----------

/// <summary>An open store: transactions, read models, event log and work queue.</summary>
public interface IStore : IDisposable
{
    string Path { get; }
    IUnitOfWork Begin();
    IQueries Queries { get; }
    IEventLog Events { get; }
    IWorkQueue Queue { get; }
    IAgentPresence Agents { get; }
}

public interface IStoreProvider
{
    /// <summary>Creates an empty store file. Fails if the file exists.</summary>
    IStore Create(string path);

    IStore Open(string path);
}

// ---------- Other infrastructure ----------

public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

public sealed record FetchedSource(string Content, string MediaType, string Locator);

/// <summary>Fetches sources blearn can reach itself: http(s) URLs and local files.</summary>
public interface ISourceFetcher
{
    Task<FetchedSource> FetchAsync(string locator, CancellationToken ct = default);
}
