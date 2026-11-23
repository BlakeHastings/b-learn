using BLearn.Domain;

namespace BLearn.Domain.Tests;

/// <summary>In-memory graph state for exercising invariants without a database.</summary>
internal sealed class FakeGraph : IGraphReader
{
    public HashSet<string> Concepts { get; } = [];
    public Dictionary<string, string> Names { get; } = [];
    public List<EdgeRecord> Edges { get; } = [];
    public HashSet<string> EvidenceIds { get; } = [];

    public FakeGraph With(params string[] ids)
    {
        foreach (var id in ids) { Concepts.Add(id); Names[BLearn.Domain.Names.Normalize(id)] = id; }
        return this;
    }

    public FakeGraph Edge(string s, string type, string t) { Edges.Add(new(s, type, t)); return this; }
    public FakeGraph WithEvidence(params string[] ids) { foreach (var i in ids) EvidenceIds.Add(i); return this; }

    public bool ConceptExists(string id) => Concepts.Contains(id);
    public string? NameOwner(string normalizedName) => Names.GetValueOrDefault(normalizedName);
    public EdgeRecord? EdgeBetween(string a, string b) =>
        Edges.FirstOrDefault(e => (e.Source == a && e.Target == b) || (e.Source == b && e.Target == a));
    public bool EvidenceExists(string id) => EvidenceIds.Contains(id);
    public HashSet<string> Cited { get; } = [];
    public bool EvidenceCited(string id) => Cited.Contains(id);
    public Dictionary<string, string> CapturedBy { get; } = [];
    public string? EvidenceCapturedBy(string id) => CapturedBy.GetValueOrDefault(id) ?? (EvidenceIds.Contains(id) ? "researcher" : null);

    public bool DownPathExists(string from, string to)
    {
        var down = EdgeTypes.DownNames.ToHashSet();
        var seen = new HashSet<string>();
        var stack = new Stack<string>([from]);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n == to) return true;
            if (!seen.Add(n)) continue;
            foreach (var e in Edges.Where(e => e.Source == n && down.Contains(e.Type))) stack.Push(e.Target);
        }
        return false;
    }
}

public sealed class ConceptTests
{
    private static string Violation(Action act) => Assert.Throws<DomainViolation>(act).Code;

    [Fact]
    public void StubRaisesCreatedEvent()
    {
        var c = Concept.CreateStub("pointer", "Pointer", "Holds an address.", ["pointers"], new FakeGraph());
        Assert.Equal(ConceptStatus.Stub, c.Status);
        Assert.IsType<ConceptStubCreated>(Assert.Single(c.PendingEvents));
    }

    [Theory]
    [InlineData("Pointer")]
    [InlineData("C Sharp")]
    public void DuplicateNameIsRejected(string name)
    {
        var g = new FakeGraph().With("pointer");
        g.Names[Names.Normalize("c-sharp")] = "csharp";
        Assert.Equal("name-duplicate", Violation(() => Concept.CreateStub("other", name, "Gloss.", [], g)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("two\nlines")]
    [InlineData("has a [[link]]")]
    public void BadGlossIsRejected(string gloss) =>
        Assert.Equal("gloss-format", Violation(() => Concept.CreateStub("x", "X", gloss, [], new FakeGraph())));

    [Fact]
    public void OverlongGlossIsRejected() =>
        Assert.Equal("gloss-format", Violation(() => Concept.CreateStub("x", "X", new string('a', 201), [], new FakeGraph())));

    [Fact]
    public void BadIdIsRejected() =>
        Assert.Equal("id-format", Violation(() => Concept.CreateStub("Not_Kebab", "X", "G.", [], new FakeGraph())));

    [Fact]
    public void FactWithoutEvidenceIsRejected()
    {
        var c = Concept.CreateStub("x", "X", "G.", [], new FakeGraph());
        Assert.Equal("fact-uncited", Violation(() => c.ReplaceFacts([new Fact("A claim.", [])], new FakeGraph())));
    }

    [Fact]
    public void FactCitingUnknownEvidenceIsRejected()
    {
        var c = Concept.CreateStub("x", "X", "G.", [], new FakeGraph());
        Assert.Equal("evidence-missing", Violation(() => c.ReplaceFacts([new Fact("A claim.", ["ev-1"])], new FakeGraph())));
    }

    [Fact]
    public void MultiParagraphFactIsRejected()
    {
        var g = new FakeGraph().WithEvidence("ev-1");
        var c = Concept.CreateStub("x", "X", "G.", [], g);
        Assert.Equal("fact-format", Violation(() => c.ReplaceFacts([new Fact("One.\n\nTwo.", ["ev-1"])], g)));
    }

    [Fact]
    public void CitedFactsMakeConceptResearched()
    {
        var g = new FakeGraph().WithEvidence("ev-1");
        var c = Concept.CreateStub("x", "X", "G.", [], g);
        c.ReplaceFacts([new Fact("A claim.", ["ev-1"])], g);
        Assert.Equal(ConceptStatus.Researched, c.Status);
    }

    [Fact]
    public void TeachingBeforeFactsIsRejected()
    {
        var c = Concept.CreateStub("x", "X", "G.", [], new FakeGraph());
        Assert.Equal("teaching-before-facts", Violation(() => c.ReplaceTeaching("S.", "E.", new FakeGraph())));
    }

    [Fact]
    public void LegacyTeachingIsAllowedWithoutFactsAndIsUnverified()
    {
        var c = Concept.CreateStub("x", "X", "G.", [], new FakeGraph());
        c.ReplaceTeaching("S.", "E.", new FakeGraph(), legacy: true);
        Assert.Equal(ConceptStatus.Unverified, c.Status);
    }

    [Fact]
    public void TeachingLinkWithoutEdgeIsRejected()
    {
        var g = new FakeGraph().With("x", "y").WithEvidence("ev-1");
        var c = Concept.CreateStub("z", "Z", "G.", [], g);
        g.With("z");
        c.ReplaceFacts([new Fact("A claim.", ["ev-1"])], g);
        Assert.Equal("link-no-edge", Violation(() => c.ReplaceTeaching("See [[y]].", "E.", g)));
    }

    [Fact]
    public void TeachingLinkToIncomingEdgeIsAllowed()
    {
        var g = new FakeGraph().With("parent").WithEvidence("ev-1");
        var c = Concept.CreateStub("child", "Child", "G.", [], g);
        g.With("child").Edge("parent", "requires", "child");
        c.ReplaceFacts([new Fact("A claim.", ["ev-1"])], g);
        c.ReplaceTeaching("Used by [[parent|the parent]].", "E.", g);
        Assert.Equal(ConceptStatus.Complete, c.Status);
    }

    [Fact]
    public void TeachingHeadingsAreRejected()
    {
        var g = new FakeGraph().WithEvidence("ev-1");
        var c = Concept.CreateStub("x", "X", "G.", [], g);
        c.ReplaceFacts([new Fact("A claim.", ["ev-1"])], g);
        Assert.Equal("teaching-format", Violation(() => c.ReplaceTeaching("S.", "## Facts\nsneaky", g)));
    }

    [Fact]
    public void ExpansionOfResearchedConceptIsRejected()
    {
        var g = new FakeGraph().WithEvidence("ev-1");
        var c = Concept.CreateStub("x", "X", "G.", [], g);
        c.ReplaceFacts([new Fact("A claim.", ["ev-1"])], g);
        Assert.Equal("already-researched", Violation(c.RequestExpansion));
    }

    [Fact]
    public void RecallOnStubIsRejected()
    {
        var c = Concept.CreateStub("x", "X", "G.", [], new FakeGraph());
        Assert.Equal("recall-on-stub", Violation(c.RequestRecall));
    }
}

public sealed class GraphTests
{
    private static string Violation(Action act) => Assert.Throws<DomainViolation>(act).Code;

    [Fact]
    public void EdgeNeedsEvidence()
    {
        var g = new Graph(new FakeGraph().With("a", "b"));
        Assert.Equal("edge-uncited", Violation(() => g.AddEdge("a", "requires", "b", [])));
    }

    [Fact]
    public void LegacyEdgeMayOmitEvidence()
    {
        var g = new Graph(new FakeGraph().With("a", "b"));
        g.AddEdge("a", "requires", "b", [], legacy: true);
        Assert.True(Assert.IsType<EdgeAdded>(Assert.Single(g.PendingEvents)).Legacy);
    }

    [Fact]
    public void UnknownTypeIsRejected()
    {
        var g = new Graph(new FakeGraph().With("a", "b").WithEvidence("e"));
        Assert.Equal("edge-type", Violation(() => g.AddEdge("a", "sort-of-like", "b", ["e"])));
    }

    [Fact]
    public void DanglingTargetIsRejected()
    {
        var g = new Graph(new FakeGraph().With("a").WithEvidence("e"));
        Assert.Equal("edge-dangling", Violation(() => g.AddEdge("a", "requires", "ghost", ["e"])));
    }

    [Fact]
    public void SelfEdgeIsRejected()
    {
        var g = new Graph(new FakeGraph().With("a").WithEvidence("e"));
        Assert.Equal("edge-self", Violation(() => g.AddEdge("a", "requires", "a", ["e"])));
    }

    [Fact]
    public void SecondRelationshipInEitherDirectionIsRejected()
    {
        var g = new Graph(new FakeGraph().With("a", "b").Edge("b", "related", "a").WithEvidence("e"));
        Assert.Equal("edge-multiple", Violation(() => g.AddEdge("a", "requires", "b", ["e"])));
    }

    [Fact]
    public void DownwardCycleIsRejected()
    {
        var reader = new FakeGraph().With("a", "b", "c").Edge("a", "built-on", "b").Edge("b", "is-a", "c").WithEvidence("e");
        Assert.Equal("edge-cycle", Violation(() => new Graph(reader).AddEdge("c", "requires", "a", ["e"])));
    }

    [Fact]
    public void SidewaysEdgeMayCloseALoop()
    {
        // csharp leads down to roslyn (via x); roslyn being written in C# is sideways, so no cycle.
        var g = new Graph(new FakeGraph().With("roslyn", "csharp", "x").Edge("csharp", "built-on", "x").Edge("x", "requires", "roslyn").WithEvidence("e"));
        g.AddEdge("roslyn", "implemented-in", "csharp", ["e"]);
        Assert.Single(g.PendingEvents);
    }
}

public sealed class EvidenceTests
{
    private static readonly StoreSettings Settings = Make();

    private static StoreSettings Make()
    {
        var s = StoreSettings.Create("Test", "root", false);
        s.RegisterSource("web", "web", "Public web pages", Trust.Captured);
        return s;
    }

    private static Evidence Capture(string quote = "A variable of a value type contains an instance of the type.") =>
        Evidence.Capture("ev-1", "web", "https://example.com", quote,
            "Docs: A variable  of a value type contains an instance of the type. More text.", "h1",
            Trust.Captured, "session-a", DateTimeOffset.Now, Settings, new FakeGraph());

    [Fact]
    public void QuoteFoundDespiteSpacingAndCase() => Assert.Equal(Trust.Captured, Capture("a VARIABLE of a value type contains").Trust);

    [Fact]
    public void QuoteMissingFromSnapshotIsRejected() =>
        Assert.Equal("quote-not-found", Assert.Throws<DomainViolation>(() => Capture("Value types always live on the stack.")).Code);

    [Fact]
    public void UnregisteredSourceIsRejected() =>
        Assert.Equal("source-unregistered", Assert.Throws<DomainViolation>(() =>
            Evidence.Capture("ev-1", "airtable", "rec1", "q", "q", "h", Trust.Captured, "s", DateTimeOffset.Now, Settings, new FakeGraph())).Code);

    [Fact]
    public void ReconfirmFromSameSessionIsRejected()
    {
        var e = Capture();
        Assert.Equal("reconfirm-same-session",
            Assert.Throws<DomainViolation>(() => e.Reconfirm("A variable of a value type contains an instance of the type.", "h2", "session-a")).Code);
    }

    [Fact]
    public void ReconfirmFromOtherSessionRaisesTrust()
    {
        var e = Capture();
        e.Reconfirm("...a variable of a value type contains an instance of the type...", "h2", "session-b");
        Assert.Equal(Trust.Reconfirmed, e.Trust);
    }

    [Fact]
    public void VerifyAgainstChangedSourceMarksStale()
    {
        var e = Capture();
        e.Verify("The page was rewritten.", "h3");
        Assert.True(e.Stale);
        Assert.IsType<EvidenceMarkedStale>(e.PendingEvents[^1]);
    }

    [Theory]
    [InlineData("It’s “quoted” — here", "it's \"quoted\" - here")]
    [InlineData("  many\n\tspaces  ", "many spaces")]
    public void NormalizationHandlesTypography(string input, string expected) =>
        Assert.Equal(expected, QuoteMatcher.Normalize(input));
}

public sealed class StoreSettingsTests
{
    [Fact]
    public void DuplicateSourceIsRejected()
    {
        var s = StoreSettings.Create("T", "root", false);
        s.RegisterSource("web", "web", "Public pages", Trust.Captured);
        Assert.Equal("source-duplicate", Assert.Throws<DomainViolation>(() => s.RegisterSource("web", "web", "Again", Trust.Captured)).Code);
    }

    [Fact]
    public void SourceNeedsScope()
    {
        var s = StoreSettings.Create("T", "root", false);
        Assert.Equal("source-scope", Assert.Throws<DomainViolation>(() => s.RegisterSource("drive", "google-drive", " ", Trust.Captured)).Code);
    }
}

public sealed class ConversationTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private static string Violation(Action act) => Assert.Throws<DomainViolation>(act).Code;
    private static Conversation Start(FakeGraph? g = null, int factCount = 2) =>
        Conversation.Start("conv-1", "pointer", "What is a pointer?", "holds an address", null, factCount, At, g ?? new FakeGraph().With("pointer"));

    [Fact]
    public void QuestionStartsAConversationAndAwaitsAReply()
    {
        var c = Start();
        var asked = Assert.IsType<QuestionAsked>(Assert.Single(c.PendingEvents));
        Assert.Equal(("conv-1", 0, "holds an address"), (asked.ConversationId, asked.MessageOrdinal, asked.Quote));
        Assert.True(c.AwaitingReply);
    }

    [Fact]
    public void FollowUpWaitsForTheReply()
    {
        var g = new FakeGraph().With("pointer");
        var c = Start(g);
        Assert.Equal("question-pending", Violation(() => c.Ask("And another thing?", At)));
        c.Reply("It stores an address.", "agent-1", At, g);
        c.Ask("And another thing?", At);
        Assert.Equal(3, c.Messages.Count);
    }

    [Fact]
    public void ReplyRules()
    {
        var g = new FakeGraph().With("pointer");
        var c = Start(g);
        Assert.Equal("session-required", Violation(() => c.Reply("Answer.", " ", At, g)));
        Assert.Equal("link-dangling", Violation(() => c.Reply("See [[ghost]].", "agent-1", At, g)));
        c.Reply("Answer.", "agent-1", At, g);
        Assert.Equal("reply-unexpected", Violation(() => c.Reply("Again.", "agent-2", At, g)));
    }

    [Fact]
    public void AnchorsAreValidated()
    {
        var g = new FakeGraph().With("pointer");
        Assert.Equal("fact-index", Violation(() => Conversation.Start("conv-1", "pointer", "Q?", null, 2, 2, At, g)));
        Assert.Equal("concept-missing", Violation(() => Conversation.Start("conv-1", "ghost", "Q?", null, null, 0, At, g)));
        Assert.Equal("question-required", Violation(() => Conversation.Start("conv-1", "pointer", "  ", null, null, 0, At, g)));
    }
}

public sealed class CheckAndQuizTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private static string Violation(Action act) => Assert.Throws<DomainViolation>(act).Code;

    private static (Concept, FakeGraph) Researched()
    {
        var g = new FakeGraph().WithEvidence("ev-1", "ev-2");
        var c = Concept.CreateStub("pointer", "Pointer", "Holds a memory address.", [], g);
        c.ReplaceFacts([new Fact("A pointer holds an address.", ["ev-1"]), new Fact("Dereferencing reads the value.", ["ev-2"])], g);
        c.ReplaceTeaching("A pointer points at memory.", "It is always fast.", g);
        return (c, g);
    }

    [Fact]
    public void CheckNeedsAVerdictForEveryFact()
    {
        var (c, g) = Researched();
        Assert.Equal("check-incomplete", Violation(() => c.RecordCheck([new FactCheck(0, Verdict.Supports, null)], [], "checker", At, g)));
        Assert.Equal("check-note-required", Violation(() => c.RecordCheck(
            [new FactCheck(0, Verdict.Supports, null), new FactCheck(1, Verdict.Partial, null)], [], "checker", At, g)));
        Assert.Equal("check-duplicate", Violation(() => c.RecordCheck(
            [new FactCheck(0, Verdict.Supports, null), new FactCheck(0, Verdict.Supports, null), new FactCheck(1, Verdict.Supports, null)], [], "checker", At, g)));
    }

    [Fact]
    public void CheckerMustNotBeTheResearcher()
    {
        var (c, g) = Researched();
        g.CapturedBy["ev-2"] = "agent-a";
        Assert.Equal("check-same-session", Violation(() => c.RecordCheck(
            [new FactCheck(0, Verdict.Supports, null), new FactCheck(1, Verdict.Supports, null)], [], "agent-a", At, g)));
    }

    [Fact]
    public void FlagsMustQuoteTheTeaching()
    {
        var (c, g) = Researched();
        FactCheck[] all = [new(0, Verdict.Supports, null), new(1, Verdict.Unsupported, "The quote says nothing about reading.")];
        Assert.Equal("flag-not-found", Violation(() => c.RecordCheck(all, [new TeachingFlag("never fast", "No fact says this.")], "checker", At, g)));
        c.RecordCheck(all, [new TeachingFlag("it is always FAST", "No fact says this.")], "checker", At, g);
        var e = Assert.IsType<FactsChecked>(c.PendingEvents[^1]);
        Assert.Equal((1, 0, 1, 1), (e.Supports, e.Partial, e.Unsupported, e.TeachingFlags));
    }

    [Fact]
    public void ReplacingFactsClearsTheCheckAndQuiz_ReplacingTeachingClearsOnlyFlags()
    {
        var (c, g) = Researched();
        c.RecordCheck([new FactCheck(0, Verdict.Supports, null), new FactCheck(1, Verdict.Supports, null)],
            [new TeachingFlag("always fast", "Unsupported.")], "checker", At, g);
        c.ReplaceRecallQuestions([new RecallQuestion("What does a pointer hold?", "An address.", [0])]);

        c.ReplaceTeaching("A pointer points at memory.", "Reading through it is dereferencing.", g);
        Assert.Empty(c.Check!.Flags);
        Assert.Equal(2, c.Check.Facts.Count);

        c.ReplaceFacts([new Fact("A pointer holds an address.", ["ev-1"])], g);
        Assert.Null(c.Check);
        Assert.Empty(c.RecallQuestions);
    }

    [Fact]
    public void QuizQuestionsMustTestExistingFacts()
    {
        var (c, _) = Researched();
        Assert.Equal("recall-uncited", Violation(() => c.ReplaceRecallQuestions([new RecallQuestion("Q?", "A.", [])])));
        Assert.Equal("fact-index", Violation(() => c.ReplaceRecallQuestions([new RecallQuestion("Q?", "A.", [5])])));
        Assert.Equal("recall-format", Violation(() => c.ReplaceRecallQuestions([new RecallQuestion("Q?", " ", [0])])));
        var stub = Concept.CreateStub("x", "X", "G.", [], new FakeGraph());
        Assert.Equal("recall-on-stub", Violation(() => stub.ReplaceRecallQuestions([new RecallQuestion("Q?", "A.", [0])])));
    }
}

public sealed class AutoLinkTests
{
    private static readonly (string, string)[] Names = [("C#", "c-sharp"), ("LINQ", "linq"), ("Language Integrated Query", "linq"), (".NET", "dotnet"), ("Java", "java")];

    [Fact]
    public void LinksTheFirstMentionOfEachConcept() =>
        Assert.Equal("Use [[linq|LINQ]] in [[c-sharp|C#]]. LINQ again.", Wikilinks.AutoLink("Use LINQ in C#. LINQ again.", Names));

    [Fact]
    public void LongerNamesWinAndCaseIsPreserved() =>
        Assert.Equal("[[linq|language integrated query]] runs on [[dotnet|.NET]].",
            Wikilinks.AutoLink("language integrated query runs on .NET.", Names));

    [Fact]
    public void LeavesCodeLinksAndPartialWordsAlone()
    {
        Assert.Equal("`LINQ` and [[java]] and JavaScript", Wikilinks.AutoLink("`LINQ` and [[java]] and JavaScript", Names));
        Assert.Equal("```\nC# code\n```", Wikilinks.AutoLink("```\nC# code\n```", Names));
    }
}
