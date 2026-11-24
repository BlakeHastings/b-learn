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
        Ok(Commands.PostStore("Test", "Root", "The root concept.", false, "root"));
        Ok(Commands.PostSource("web", "web", "Public web pages"));
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
        Ok(Commands.PostEvidenceAsync(new EvidenceInput(id, "web", quote, "session-a", Locator: "https://example.com/" + id, SnapshotText: $"Page text. {quote} More.", MediaType: "text/plain")).GetAwaiter().GetResult());
}

public sealed class RepositoryTests : StoreFixture
{
    [Fact]
    public void CommandReturnsStoredEventsWithSequence()
    {
        var r = Ok(Commands.PostConcept(new ConceptInput("pointer", "Pointer", "Holds an address.", [])));
        var e = Assert.Single(r.Events);
        Assert.Equal("ConceptStubCreated", e.Type);
        Assert.True(e.Seq > 0);
        Assert.Equal(e.Seq, Store.Events.LatestSeq());
    }

    [Fact]
    public void ConceptRoundTripsWithFactsAndTeaching()
    {
        Ok(Commands.PostConcept(new ConceptInput("pointer", "Pointer", "Holds an address.", ["pointers"])));
        Evidence("ev-1");
        Ok(Commands.PutFacts("pointer", [new FactInput("The quoted claim.", ["ev-1"])]));
        Ok(Commands.PutTeaching("pointer", "A summary.", "An explanation."));

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
        Ok(Commands.PostConcept(new ConceptInput("pointer", "Pointer", "G.", [])));
        Rejected("name-duplicate", Commands.PostConcept(new ConceptInput("pointers", "pointer", "G.", [])));
    }

    [Fact]
    public void DatabaseBackstopRejectsDuplicateNamesEvenIfDomainIsBypassed()
    {
        Ok(Commands.PostConcept(new ConceptInput("pointer", "Pointer", "G.", [])));
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
        Ok(Commands.PostConcept(new ConceptInput("a", "A", "G.", [])));
        Ok(Commands.PostConcept(new ConceptInput("b", "B", "G.", [])));
        Evidence("ev-1");
        Ok(Commands.PostEdge("a", "requires", "b", ["ev-1"]));
        using var c = new SqliteConnection($"Data Source={Store.Path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO edges(source, target, type) VALUES ('b', 'a', 'related')";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void DownwardCycleIsRejectedThroughSqlPathQuery()
    {
        foreach (var id in new[] { "a", "b", "c" }) Ok(Commands.PostConcept(new ConceptInput(id, id.ToUpperInvariant(), "G.", [])));
        Evidence("ev-1");
        Ok(Commands.PostEdge("a", "built-on", "b", ["ev-1"]));
        Ok(Commands.PostEdge("b", "requires", "c", ["ev-1"]));
        Rejected("edge-cycle", Commands.PostEdge("c", "is-a", "a", ["ev-1"]));
        Ok(Commands.PostEdge("c", "related", "a", ["ev-1"]));
    }

    [Fact]
    public void RejectedCommandLeavesNoTrace()
    {
        var before = Store.Events.LatestSeq();
        Rejected("edge-dangling", Commands.PostConcept(new ConceptInput("x", "X", "G.", [], new ConceptLink("ghost", "requires", ["nope"]))));
        Assert.Equal(before, Store.Events.LatestSeq());
        Assert.Null(Store.Queries.GetConcept("x"));
    }

    [Fact]
    public void NeighborsShowDirectionFromEachSide()
    {
        Ok(Commands.PostConcept(new ConceptInput("pointer", "Pointer", "G.", [])));
        Evidence("ev-1");
        Ok(Commands.PostConcept(new ConceptInput("memory-address", "Memory address", "G.", [], new ConceptLink("pointer", "built-on", ["ev-1"]))));
        var fromPointer = Assert.Single(Store.Queries.Neighbors("pointer"));
        Assert.Equal(("down", "built-on"), (fromPointer.Direction, fromPointer.Label));
        var fromAddress = Assert.Single(Store.Queries.Neighbors("memory-address"));
        Assert.Equal(("up", "underlies"), (fromAddress.Direction, fromAddress.Label));
    }

    [Fact]
    public void EvidenceFromUnregisteredSourceIsRejected() =>
        Rejected("source-unregistered", Commands.PostEvidenceAsync(new EvidenceInput("ev-x", "airtable", "q", "s", Locator: "rec1", SnapshotText: "q", MediaType: "text/plain")).GetAwaiter().GetResult());

    [Fact]
    public void ReconfirmationRaisesTrust()
    {
        Evidence("ev-1");
        Rejected("reconfirm-same-session", Commands.PostConfirmation("ev-1", "The quoted claim.", "text/plain", "session-a"));
        Ok(Commands.PostConfirmation("ev-1", "...the quoted claim...", "text/plain", "session-b"));
        Assert.Equal("reconfirmed", Store.Queries.GetEvidence("ev-1")!.Trust);
    }

    [Fact]
    public async Task FetchedEvidenceFromLocalFileIsTrustedAndReverifiable()
    {
        Ok(Commands.PostSource("docs", "file", "Local documents"));
        var file = Path.Combine(Dir, "contract.html");
        await File.WriteAllTextAsync(file, "<html><body><p>Seniority shall be determined by <b>continuous</b> length of service.</p></body></html>");
        Ok(await Commands.PostEvidenceAsync(new EvidenceInput("ev-doc", "docs", "determined by continuous length of service", "session-a", Url: file)));
        Assert.Equal("fetched", Store.Queries.GetEvidence("ev-doc")!.Trust);

        await File.WriteAllTextAsync(file, "<p>Seniority is determined by job classification.</p>");
        Ok(await Commands.PostVerificationAsync("ev-doc"));
        Assert.True(Store.Queries.GetEvidence("ev-doc")!.Stale);
    }

    [Fact]
    public void RequestQueuesWorkThroughPolicyAndDeduplicates()
    {
        Ok(Commands.PostConcept(new ConceptInput("pointer", "Pointer", "G.", [])));
        Ok(Commands.PostRequest("pointer", new RequestInput("expand")));
        Ok(Commands.PostRequest("pointer", new RequestInput("expand")));
        var msg = Assert.Single(Store.Queue.List());
        Assert.Equal("expand", msg.Queue);
        Assert.Equal("pointer", JsonDocument.Parse(msg.PayloadJson).RootElement.GetProperty("conceptId").GetString());
        Assert.Single(Store.Queries.GetConcept("pointer")!.OpenWork);
    }

    [Fact]
    public void ExportWritesOneFilePerConcept()
    {
        Ok(Commands.PostConcept(new ConceptInput("pointer", "Pointer", "Holds an address.", [])));
        var outDir = Path.Combine(Dir, "export");
        Assert.Equal(2, MarkdownExporter.Export(Store.Queries, outDir));
        Assert.Contains("gloss: Holds an address.", File.ReadAllText(Path.Combine(outDir, "notes", "pointer.md")));
    }
}

public sealed class QueueTests : StoreFixture
{
    private void Request(string id)
    {
        Ok(Commands.PostConcept(new ConceptInput(id, id, "G.", [])));
        Ok(Commands.PostRequest(id, new RequestInput("expand")));
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
            Clock.Now = Clock.Now.AddHours(1);
        }
        Assert.Null(Store.Queue.TryReceive([], TimeSpan.FromMinutes(1), "w"));
        Assert.Equal("boom", Assert.Single(Store.Queue.DeadLetters()).LastError);
    }

    [Fact]
    public void ReleasedMessageWaitsBeforeRedelivery()
    {
        Request("a");
        var m = Store.Queue.TryReceive([], TimeSpan.FromMinutes(30), "w1")!;
        Assert.True(Store.Queue.Nack(m.Receipt!, "cannot do this kind"));
        Assert.Null(Store.Queue.TryReceive([], TimeSpan.FromMinutes(30), "w1"));
        Assert.Single(Store.Queue.List());
        Clock.Now = Clock.Now.AddMinutes(2);
        Assert.Equal(m.Id, Store.Queue.TryReceive([], TimeSpan.FromMinutes(30), "w2")!.Id);
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
        Ok(Commands.PostImport(notes));

        var p = Store.Queries.GetConcept("pointer")!;
        Assert.Equal("unverified", p.Status);
        Assert.True(p.TeachingLegacy);
        Assert.True(Assert.Single(p.Neighbors).Legacy);
        Assert.Equal(1, Store.Queries.GetInfo().LegacyEdges);
    }
}

public sealed class ResourceCommandTests : StoreFixture
{
    [Fact]
    public void PatchConceptChangesTitleGlossAndAliasesTogether()
    {
        Ok(Commands.PostConcept(new ConceptInput("pointer", "Pointer", "Holds an address.")));
        var r = Ok(Commands.PatchConcept("pointer", new ConceptPatch("Pointer type", "A variable holding an address.", ["ptr"])));

        Assert.Equal(["ConceptRenamed", "GlossChanged", "AliasAdded"], r.Events.Select(e => e.Type));
        var c = Store.Queries.GetConcept("pointer")!;
        Assert.Equal("Pointer type", c.Title);
        Assert.Equal("A variable holding an address.", c.Gloss);
        Assert.Contains("ptr", c.Aliases);
    }

    [Fact]
    public void PatchConceptIsAllOrNothing()
    {
        Ok(Commands.PostConcept(new ConceptInput("pointer", "Pointer", "G.")));
        Ok(Commands.PostConcept(new ConceptInput("array", "Array", "G.")));
        var before = Store.Events.LatestSeq();

        // The title change is valid, the alias is taken: nothing may commit.
        Rejected("name-duplicate", Commands.PatchConcept("pointer", new ConceptPatch("Pointer type", AddAliases: ["array"])));

        Assert.Equal("Pointer", Store.Queries.GetConcept("pointer")!.Title);
        Assert.Equal(before, Store.Events.LatestSeq());
    }

    [Fact]
    public void PatchWithNothingToChangeIsRejected()
    {
        Ok(Commands.PostConcept(new ConceptInput("pointer", "Pointer", "G.")));
        Rejected("patch-empty", Commands.PatchConcept("pointer", new ConceptPatch()));
        Rejected("patch-empty", Commands.PatchStore(null));
    }

    [Fact]
    public void PutFactsReplacesTheWholeList()
    {
        Evidence("ev-1");
        Ok(Commands.PostConcept(new ConceptInput("pointer", "Pointer", "G.")));
        Ok(Commands.PutFacts("pointer", [new("First.", ["ev-1"]), new("Second.", ["ev-1"])]));
        var r = Ok(Commands.PutFacts("pointer", [new("Only.", ["ev-1"])]));

        Assert.Equal("FactsReplaced", Assert.Single(r.Events).Type);
        Assert.Equal("Only.", Assert.Single(Store.Queries.GetConcept("pointer")!.Facts).Text);
    }

    [Fact]
    public void UncitedEvidenceCanBeDeletedButCitedEvidenceCannot()
    {
        Evidence("ev-used");
        Evidence("ev-orphan");
        Ok(Commands.PostConcept(new ConceptInput("pointer", "Pointer", "G.")));
        Ok(Commands.PutFacts("pointer", [new("A claim.", ["ev-used"])]));
        Ok(Commands.PostEdge("root", "built-on", "pointer", ["ev-orphan"]));

        Rejected("evidence-cited", Commands.DeleteEvidence("ev-used"));
        Rejected("evidence-cited", Commands.DeleteEvidence("ev-orphan"));

        Ok(Commands.DeleteEdge("root", "pointer"));
        var r = Ok(Commands.DeleteEvidence("ev-orphan"));
        Assert.Equal("EvidenceRemoved", Assert.Single(r.Events).Type);
        Assert.Null(Store.Queries.GetEvidence("ev-orphan"));
        Assert.NotNull(Store.Queries.GetEvidence("ev-used"));
        Rejected("evidence-missing", Commands.DeleteEvidence("ev-orphan"));
    }

    [Fact]
    public async Task EvidenceNeedsExactlyOneOfSnapshotOrUrl()
    {
        Rejected("evidence-input", await Commands.PostEvidenceAsync(new EvidenceInput("ev-a", "web", "q", "s")));
        Rejected("evidence-input", await Commands.PostEvidenceAsync(
            new EvidenceInput("ev-b", "web", "q", "s", Locator: "x", SnapshotText: "q", Url: "https://example.com")));
    }

    [Fact]
    public void RequestKindSelectsTheEventAndUnknownKindsAreRejected()
    {
        Ok(Commands.PostConcept(new ConceptInput("pointer", "Pointer", "G.")));
        Assert.Equal("ExpansionRequested", Assert.Single(Ok(Commands.PostRequest("pointer", new RequestInput("expand"))).Events).Type);
        Rejected("kind-invalid", Commands.PostRequest("pointer", new RequestInput("ask", Text: "Why?")));
        Rejected("kind-invalid", Commands.PostRequest("pointer", new RequestInput("summarize")));
    }

    [Fact]
    public void LearnerKnownIsPutAndDeleted()
    {
        Ok(Commands.PutKnown("root"));
        Assert.Contains("root", Store.Queries.Known());
        Ok(Commands.DeleteKnown("root"));
        Assert.DoesNotContain("root", Store.Queries.Known());
    }

    [Fact]
    public void NeighborsCarryTheStoredDirectionOfTheEdge()
    {
        Evidence("ev-1");
        Ok(Commands.PostConcept(new ConceptInput("pointer", "Pointer", "G.", Link: new ConceptLink("root", "built-on", ["ev-1"]))));
        var seenFromPointer = Assert.Single(Store.Queries.Neighbors("pointer"));
        Assert.Equal(("root", "pointer"), (seenFromPointer.Source, seenFromPointer.Target));
        Assert.Equal("underlies", seenFromPointer.Label);
    }
}

public sealed class SchemaUpgradeTests : StoreFixture
{
    [Fact]
    public void Version1StoresAreUpgradedAndOldEventNamesRenamed()
    {
        Evidence("ev-1");
        Ok(Commands.PostConcept(new ConceptInput("pointer", "Pointer", "G.")));
        Ok(Commands.PutFacts("pointer", [new("A claim.", ["ev-1"])]));
        using (var c = new SqliteConnection($"Data Source={Store.Path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                UPDATE events SET type = 'FactsSubmitted', payload = json_set(payload, '$.type', 'FactsSubmitted') WHERE type = 'FactsReplaced';
                UPDATE meta SET value = '1' WHERE key = 'schema_version';
                """;
            cmd.ExecuteNonQuery();
        }

        using var reopened = new SqliteStoreProvider(Clock).Open(Store.Path);
        var events = reopened.Events.Read(0, null, 1000);
        Assert.Contains(events, e => e.Type == "FactsReplaced");
        Assert.DoesNotContain(events, e => e.Type == "FactsSubmitted" || e.PayloadJson.Contains("FactsSubmitted"));
    }
}

public sealed class ConversationAndCheckTests : StoreFixture
{
    private void Researched()
    {
        Evidence("ev-1", "A pointer holds an address.");
        Evidence("ev-2", "Dereferencing reads the value.");
        Ok(Commands.PostConcept(new ConceptInput("pointer", "Pointer", "Holds a memory address.")));
        Ok(Commands.PutFacts("pointer", [new("A pointer holds an address.", ["ev-1"]), new("Dereferencing reads the value.", ["ev-2"])]));
        Ok(Commands.PutTeaching("pointer", "A pointer points at memory.", "It is always fast."));
    }

    [Fact]
    public void QuestionIsQueuedAheadOfOtherWorkAndTheReplyIsStored()
    {
        Researched();
        Ok(Commands.PostConcept(new ConceptInput("array", "Array", "G.")));
        Ok(Commands.PostRequest("array", new RequestInput("expand")));
        var asked = Ok(Commands.PostQuestion(new QuestionInput("pointer", "Why use one?", Quote: "points at memory", FactIndex: 0)));
        var conversationId = JsonDocument.Parse(asked.Events.Single().PayloadJson).RootElement.GetProperty("conversationId").GetString()!;

        var work = Store.Queue.TryReceive([], TimeSpan.FromMinutes(5), "agent")!;
        Assert.Equal("ask", work.Queue);
        Assert.Contains(conversationId, work.PayloadJson);

        Rejected("question-pending", Commands.PostQuestion(new QuestionInput("pointer", "Hello?", ConversationId: conversationId)));
        Ok(Commands.PostReply(conversationId, "To share data without copying it. See [[array]].", "agent"));
        Ok(Commands.PostQuestion(new QuestionInput("pointer", "Is it fast?", ConversationId: conversationId)));

        var view = Store.Queries.GetConversation(conversationId)!;
        Assert.Equal(["learner", "agent", "learner"], view.Messages.Select(m => m.Role));
        Assert.Equal(("points at memory", 0, "A pointer holds an address."), (view.Quote, view.FactIndex, view.FactText));
        Assert.True(view.AwaitingReply);
        Assert.Single(Store.Queries.ListConversations("pointer"));
        Assert.Empty(Store.Queries.ListConversations("array"));
    }

    [Fact]
    public void FactsAndTeachingQueueOneCheck_CheckRoundTripsAndIsClearedByNewFacts()
    {
        Researched();
        var checks = Store.Queue.List().Where(m => m.Queue == "check").ToList();
        Assert.Single(checks);

        Rejected("check-same-session", Commands.PutCheck("pointer", new CheckInput([new(0, "supports", null), new(1, "supports", null)], []), "session-a"));
        Rejected("verdict-invalid", Commands.PutCheck("pointer", new CheckInput([new(0, "fine", null), new(1, "supports", null)], []), "checker"));
        Ok(Commands.PutCheck("pointer", new CheckInput(
            [new(0, "supports", null), new(1, "partial", "Says reads, quote says nothing of writes.")],
            [new("always fast", "No fact covers speed.")]), "checker"));

        // An unrelated save must keep the check (the repository rewrites facts on every save).
        Ok(Commands.PatchConcept("pointer", new ConceptPatch(AddAliases: ["ptr"])));
        var c = Store.Queries.GetConcept("pointer")!;
        Assert.Equal(["supports", "partial"], c.Facts.Select(f => f.Check!.Verdict));
        Assert.Equal("always fast", Assert.Single(c.Check!.TeachingFlags).Excerpt);

        Ok(Commands.PutFacts("pointer", [new("A pointer holds an address.", ["ev-1"])]));
        c = Store.Queries.GetConcept("pointer")!;
        Assert.Null(c.Check);
        Assert.Null(Assert.Single(c.Facts).Check);
    }

    [Fact]
    public void QuizQuestionsPersistAndAnswersAreRecorded()
    {
        Researched();
        Ok(Commands.PutRecallQuestions("pointer", [new("What does a pointer hold?", "An address.", [0])]));
        var q = Assert.Single(Store.Queries.GetConcept("pointer")!.RecallQuestions);
        Assert.Equal(("What does a pointer hold?", 0), (q.Prompt, q.FactIndexes.Single()));
        var r = Ok(Commands.PostRecallAnswer("pointer", "an address", null, 0, true));
        Assert.Contains("\"correct\":true", r.Events.Single().PayloadJson);
    }

    [Fact]
    public void Version2StoresGainTheNewTables()
    {
        using (var c = new SqliteConnection($"Data Source={Store.Path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                DROP TABLE teaching_flags; DROP TABLE fact_checks; DROP TABLE concept_checks; DROP TABLE recall_questions;
                DROP TABLE conversation_messages; DROP TABLE conversations;
                ALTER TABLE learner_recall DROP COLUMN question_index; ALTER TABLE learner_recall DROP COLUMN correct;
                UPDATE meta SET value = '2' WHERE key = 'schema_version';
                """;
            cmd.ExecuteNonQuery();
        }
        using var reopened = new SqliteStoreProvider(Clock).Open(Store.Path);
        var commands = new BLearnCommands(reopened, Clock, new SourceFetcher(SourceFetcher.CreateClient()));
        Ok(commands.PostQuestion(new QuestionInput("root", "What is this?")));
        Ok(commands.PostRecallAnswer("root", "x", null, 0, false));
    }
}

public sealed class HtmlTextTests
{
    [Fact]
    public void KeepsMainContentAndDropsChromeFootnotesAndAttributeMarkup()
    {
        var html = """
            <html><body><nav>Main menu Donate</nav>
            <main><div data-mw='{"wt":"<ref>{{cite}}</ref> > noise"}'>
            <p>The principal designers were<sup class="reference">[12]</sup> Anders Hejlsberg <span class="mw-editsection">[edit]</span>and others.</p>
            </div></main><footer>Privacy policy</footer></body></html>
            """;
        var text = HtmlText.Extract(html);
        Assert.Contains("The principal designers were Anders Hejlsberg and others.", text.Replace("  ", " "));
        Assert.DoesNotContain("Main menu", text);
        Assert.DoesNotContain("noise", text);
        Assert.DoesNotContain("[12]", text);
        Assert.DoesNotContain("Privacy", text);
    }
}

public sealed class ConceptNameTests : StoreFixture
{
    [Fact]
    public void NamesIncludeTitlesThatMatchTheirIdAndAliases()
    {
        Ok(Commands.PostConcept(new ConceptInput("microsoft", "Microsoft", "A company.", ["MSFT"])));
        var names = Store.Queries.ConceptNames();
        Assert.Contains(("Microsoft", "microsoft"), names);
        Assert.Contains(("MSFT", "microsoft"), names);
    }
}

public sealed class PresenceTests : StoreFixture
{
    [Fact]
    public void WaitingAgentIsListeningUntilItGoesQuiet()
    {
        Assert.False(Store.Agents.Status().Connected);
        Assert.Null(Store.Queue.TryReceive([], TimeSpan.FromMinutes(5), "agent-a"));
        var a = Assert.Single(Store.Agents.Status().Agents);
        Assert.Equal(("agent-a", "listening"), (a.Session, a.State));
        Assert.True(Store.Agents.Status().Connected);

        Clock.Now = Clock.Now.AddMinutes(1);
        Assert.Equal("offline", Store.Agents.Status().Agents.Single().State);
        Assert.False(Store.Agents.Status().Connected);
    }

    [Fact]
    public void AgentHoldingWorkIsWorkingAndShowsWhatItIsOn()
    {
        Ok(Commands.PostQuestion(new QuestionInput("root", "What is this?")));
        var m = Store.Queue.TryReceive([], TimeSpan.FromMinutes(30), "agent-a")!;
        Store.Agents.Heartbeat("agent-a", activity: "Reading learn.microsoft.com");
        Clock.Now = Clock.Now.AddMinutes(5);   // thinking for a while: still working

        var a = Store.Agents.Status().Agents.Single();
        Assert.Equal(("working", "ask", "root", "Reading learn.microsoft.com"), (a.State, a.Work!.Queue, a.Work.ConceptId, a.Activity));

        Assert.True(Store.Queue.Ack(m.Receipt!));
        Assert.Equal("offline", Store.Agents.Status().Agents.Single().State);   // done and not waiting again
    }

    [Fact]
    public void LeavingIsImmediate()
    {
        Store.Queue.TryReceive([], TimeSpan.FromMinutes(5), "agent-a");
        Store.Agents.Leave("agent-a");
        Assert.False(Store.Agents.Status().Connected);
    }
}

public sealed class WorkOutcomeTests : StoreFixture
{
    [Fact]
    public void FinishingIsRefusedUntilTheResultIsInTheStore()
    {
        var asked = Ok(Commands.PostQuestion(new QuestionInput("root", "What is this?")));
        var id = JsonDocument.Parse(asked.Events.Single().PayloadJson).RootElement.GetProperty("conversationId").GetString()!;
        var m = Store.Queue.TryReceive([], TimeSpan.FromMinutes(5), "agent")!;

        Rejected("outcome-missing", WorkOutcomes.Complete(Store, m.Receipt!, null));
        Ok(Commands.PostReply(id, "It is the root concept.", "agent-1"));
        Ok(WorkOutcomes.Complete(Store, m.Receipt!, null));
        Assert.Empty(Store.Queue.List());
    }

    [Fact]
    public void AnAgentCanFinishWithoutAResultByStatingWhy()
    {
        Ok(Commands.PostConcept(new ConceptInput("obscure", "Obscure thing", "G.")));
        Ok(Commands.PostRequest("obscure", new RequestInput("expand")));
        var m = Store.Queue.TryReceive([], TimeSpan.FromMinutes(5), "agent")!;
        Rejected("outcome-missing", WorkOutcomes.Complete(Store, m.Receipt!, "  "));
        Ok(WorkOutcomes.Complete(Store, m.Receipt!, "No source in scope mentions it."));
        var done = Store.Queue.List(includeDone: true).Single(x => x.Id == m.Id);
        Assert.Equal(("done", "No source in scope mentions it."), (done.State, done.LastError));
        Rejected("lease-lost", WorkOutcomes.Complete(Store, m.Receipt!, null));
    }
}
