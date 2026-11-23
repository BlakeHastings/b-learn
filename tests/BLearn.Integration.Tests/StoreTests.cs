using System.Text.Json;
using BLearn.Application;
using BLearn.Infrastructure.Fetching;
using BLearn.Infrastructure.Markdown;
using BLearn.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace BLearn.Integration.Tests;

public sealed class TestClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>A fresh store file per test, initialized with a root concept and a "web" source.</summary>
public abstract class StoreFixture : IDisposable
{
    protected readonly string Dir = Path.Combine(Path.GetTempPath(), "blearn-it", Guid.NewGuid().ToString("N"));
    protected readonly TestClock Clock = new();
    protected readonly IStore Store;
    protected readonly BLearnCommands Commands;

    protected StoreFixture()
    {
        Directory.CreateDirectory(Dir);
        Store = new SqliteStoreProvider(Clock).Create(Path.Combine(Dir, "test.blearn"));
        Commands = new BLearnCommands(Store, Clock, new SourceFetcher(SourceFetcher.CreateClient()));
        Ok(Commands.InitializeStore("Test", "Root", "The root concept.", false, "root"));
        Ok(Commands.RegisterSource("web", "web", "Public web pages"));
    }

    public void Dispose()
    {
        Store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }

    protected static CommandResult Ok(CommandResult r)
    {
        Assert.True(r.Accepted, $"{r.Violation}: {r.Message}");
        return r;
    }

    protected static void Rejected(string code, CommandResult r)
    {
        Assert.False(r.Accepted, "Expected rejection but the command was accepted.");
        Assert.Equal(code, r.Violation);
    }

    protected void Evidence(string id, string quote = "The quoted claim.") =>
        Ok(Commands.CaptureEvidence(id, "web", "https://example.com/" + id, quote, $"Page text. {quote} More.", "text/plain", "session-a"));
}

public sealed class RepositoryTests : StoreFixture
{
    [Fact]
    public void CommandReturnsStoredEventsWithSequence()
    {
        var r = Ok(Commands.CreateStub("pointer", "Pointer", "Holds an address.", []));
        var e = Assert.Single(r.Events);
        Assert.Equal("ConceptStubCreated", e.Type);
        Assert.True(e.Seq > 0);
        Assert.Equal(e.Seq, Store.Events.LatestSeq());
    }

    [Fact]
    public void ConceptRoundTripsWithFactsAndTeaching()
    {
        Ok(Commands.CreateStub("pointer", "Pointer", "Holds an address.", ["pointers"]));
        Evidence("ev-1");
        Ok(Commands.SubmitFacts("pointer", [new FactInput("The quoted claim.", ["ev-1"])]));
        Ok(Commands.SubmitTeaching("pointer", "A summary.", "An explanation."));

        var c = Store.Queries.GetConcept("pointer")!;
        Assert.Equal("complete", c.Status);
        Assert.Equal(["pointers"], c.Aliases);
        var fact = Assert.Single(c.Facts);
        Assert.True(fact.Verified);
        Assert.Equal("ev-1", Assert.Single(fact.Evidence).Id);
    }

    [Fact]
    public void DuplicateNameIsRejectedByDomain()
    {
        Ok(Commands.CreateStub("pointer", "Pointer", "G.", []));
        Rejected("name-duplicate", Commands.CreateStub("pointers", "pointer", "G.", []));
    }

    [Fact]
    public void DatabaseBackstopRejectsDuplicateNamesEvenIfDomainIsBypassed()
    {
        Ok(Commands.CreateStub("pointer", "Pointer", "G.", []));
        using var c = new SqliteConnection($"Data Source={Store.Path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO concepts(id, title, gloss, version) VALUES ('other', 'Other', 'G.', 1); " +
                          "INSERT INTO concept_names(normalized, concept_id, name, kind) VALUES ('pointer', 'other', 'Pointer', 'title');";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void DatabaseBackstopRejectsSecondEdgeForPair()
    {
        Ok(Commands.CreateStub("a", "A", "G.", []));
        Ok(Commands.CreateStub("b", "B", "G.", []));
        Evidence("ev-1");
        Ok(Commands.AddEdge("a", "requires", "b", ["ev-1"]));
        using var c = new SqliteConnection($"Data Source={Store.Path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO edges(source, target, type) VALUES ('b', 'a', 'related')";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void DownwardCycleIsRejectedThroughSqlPathQuery()
    {
        foreach (var id in new[] { "a", "b", "c" }) Ok(Commands.CreateStub(id, id.ToUpperInvariant(), "G.", []));
        Evidence("ev-1");
        Ok(Commands.AddEdge("a", "built-on", "b", ["ev-1"]));
        Ok(Commands.AddEdge("b", "requires", "c", ["ev-1"]));
        Rejected("edge-cycle", Commands.AddEdge("c", "is-a", "a", ["ev-1"]));
        Ok(Commands.AddEdge("c", "related", "a", ["ev-1"]));
    }

    [Fact]
    public void RejectedCommandLeavesNoTrace()
    {
        var before = Store.Events.LatestSeq();
        Rejected("edge-dangling", Commands.CreateStub("x", "X", "G.", [], new StubLink("ghost", "requires", ["nope"])));
        Assert.Equal(before, Store.Events.LatestSeq());
        Assert.Null(Store.Queries.GetConcept("x"));
    }

    [Fact]
    public void NeighborsShowDirectionFromEachSide()
    {
        Ok(Commands.CreateStub("pointer", "Pointer", "G.", []));
        Evidence("ev-1");
        Ok(Commands.CreateStub("memory-address", "Memory address", "G.", [], new StubLink("pointer", "built-on", ["ev-1"])));
        var fromPointer = Assert.Single(Store.Queries.Neighbors("pointer"));
        Assert.Equal(("down", "built-on"), (fromPointer.Direction, fromPointer.Label));
        var fromAddress = Assert.Single(Store.Queries.Neighbors("memory-address"));
        Assert.Equal(("up", "underlies"), (fromAddress.Direction, fromAddress.Label));
    }

    [Fact]
    public void EvidenceFromUnregisteredSourceIsRejected() =>
        Rejected("source-unregistered", Commands.CaptureEvidence("ev-x", "airtable", "rec1", "q", "q", "text/plain", "s"));

    [Fact]
    public void ReconfirmationRaisesTrust()
    {
        Evidence("ev-1");
        Rejected("reconfirm-same-session", Commands.ReconfirmEvidence("ev-1", "The quoted claim.", "text/plain", "session-a"));
        Ok(Commands.ReconfirmEvidence("ev-1", "...the quoted claim...", "text/plain", "session-b"));
        Assert.Equal("reconfirmed", Store.Queries.GetEvidence("ev-1")!.Trust);
    }

    [Fact]
    public async Task FetchedEvidenceFromLocalFileIsTrustedAndReverifiable()
    {
        Ok(Commands.RegisterSource("docs", "file", "Local documents"));
        var file = Path.Combine(Dir, "contract.html");
        await File.WriteAllTextAsync(file, "<html><body><p>Seniority shall be determined by <b>continuous</b> length of service.</p></body></html>");
        Ok(await Commands.FetchEvidenceAsync("ev-doc", "docs", file, "determined by continuous length of service", "session-a"));
        Assert.Equal("fetched", Store.Queries.GetEvidence("ev-doc")!.Trust);

        await File.WriteAllTextAsync(file, "<p>Seniority is determined by job classification.</p>");
        Ok(await Commands.VerifyEvidenceAsync("ev-doc"));
        Assert.True(Store.Queries.GetEvidence("ev-doc")!.Stale);
    }

    [Fact]
    public void RequestQueuesWorkThroughPolicyAndDeduplicates()
    {
        Ok(Commands.CreateStub("pointer", "Pointer", "G.", []));
        Ok(Commands.RequestExpansion("pointer"));
        Ok(Commands.RequestExpansion("pointer"));
        var msg = Assert.Single(Store.Queue.List());
        Assert.Equal("expand", msg.Queue);
        Assert.Equal("pointer", JsonDocument.Parse(msg.PayloadJson).RootElement.GetProperty("conceptId").GetString());
        Assert.Single(Store.Queries.GetConcept("pointer")!.OpenWork);
    }

    [Fact]
    public void ExportWritesOneFilePerConcept()
    {
        Ok(Commands.CreateStub("pointer", "Pointer", "Holds an address.", []));
        var outDir = Path.Combine(Dir, "export");
        Assert.Equal(2, MarkdownExporter.Export(Store.Queries, outDir));
        Assert.Contains("gloss: Holds an address.", File.ReadAllText(Path.Combine(outDir, "notes", "pointer.md")));
    }
}

public sealed class QueueTests : StoreFixture
{
    private void Request(string id)
    {
        Ok(Commands.CreateStub(id, id, "G.", []));
        Ok(Commands.RequestExpansion(id));
    }

    [Fact]
    public void ConcurrentReceiversNeverGetTheSameMessage()
    {
        for (var i = 0; i < 20; i++) Request($"c{i}");
        var received = new System.Collections.Concurrent.ConcurrentBag<long>();
        Parallel.For(0, 8, w =>
        {
            while (Store.Queue.TryReceive([], TimeSpan.FromMinutes(5), $"worker-{w}") is { } m) received.Add(m.Id);
        });
        Assert.Equal(20, received.Count);
        Assert.Equal(20, received.Distinct().Count());
    }

    [Fact]
    public void ExpiredLeaseIsRedeliveredAndOldReceiptIsDead()
    {
        Request("a");
        var first = Store.Queue.TryReceive([], TimeSpan.FromSeconds(30), "w1")!;
        Assert.Null(Store.Queue.TryReceive([], TimeSpan.FromSeconds(30), "w2"));
        Clock.Now = Clock.Now.AddMinutes(1);
        var second = Store.Queue.TryReceive([], TimeSpan.FromSeconds(30), "w2")!;
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.Attempts);
        Assert.False(Store.Queue.Ack(first.Receipt!));
        Assert.True(Store.Queue.Ack(second.Receipt!));
        Assert.Empty(Store.Queue.List());
    }

    [Fact]
    public void RepeatedFailureDeadLetters()
    {
        Request("a");
        for (var i = 0; i < MaxAttempts; i++)
        {
            var m = Store.Queue.TryReceive([], TimeSpan.FromMinutes(1), "w")!;
            Assert.True(Store.Queue.Nack(m.Receipt!, "boom"));
        }
        Assert.Null(Store.Queue.TryReceive([], TimeSpan.FromMinutes(1), "w"));
        Assert.Equal("boom", Assert.Single(Store.Queue.DeadLetters()).LastError);
    }

    [Fact]
    public void ReceiveFiltersByQueue()
    {
        Request("a");
        Assert.Null(Store.Queue.TryReceive(["teach"], TimeSpan.FromMinutes(1), "w"));
        Assert.NotNull(Store.Queue.TryReceive(["expand"], TimeSpan.FromMinutes(1), "w"));
    }

    private const int MaxAttempts = 5; // mirrors SqliteWorkQueue.MaxAttempts
}

public sealed class ImportTests : StoreFixture
{
    [Fact]
    public void LegacyNotesImportAsUnverified()
    {
        var notesDir = Path.Combine(Dir, "legacy", "notes");
        Directory.CreateDirectory(notesDir);
        File.WriteAllText(Path.Combine(notesDir, "pointer.md"), """
            ---
            id: pointer
            title: Pointer
            status: complete
            gloss: Holds an address.
            edges:
              built-on: [memory-address]
            ---
            ## Summary

            A pointer holds a [[memory-address]].

            ## Detail

            More detail.
            """);
        File.WriteAllText(Path.Combine(notesDir, "memory-address.md"), "---\nid: memory-address\ntitle: Memory address\nstatus: stub\ngloss: A location.\n---\n");

        var (notes, problems) = LegacyMarkdownReader.ReadFolder(Path.Combine(Dir, "legacy"));
        Assert.Empty(problems);
        Ok(Commands.ImportLegacy(notes));

        var p = Store.Queries.GetConcept("pointer")!;
        Assert.Equal("unverified", p.Status);
        Assert.True(p.TeachingLegacy);
        Assert.True(Assert.Single(p.Neighbors).Legacy);
        Assert.Equal(1, Store.Queries.GetInfo().LegacyEdges);
    }
}
