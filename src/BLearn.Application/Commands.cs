using BLearn.Domain;

namespace BLearn.Application;

/// <summary>The outcome of a command: the events it emitted, or the invariant it broke.</summary>
public sealed record CommandResult(bool Accepted, IReadOnlyList<StoredEvent> Events, string? Violation, string? Message)
{
    public static CommandResult Ok(IReadOnlyList<StoredEvent> events) => new(true, events, null, null);
    public static CommandResult Rejected(string code, string message) => new(false, [], code, message);
}

public sealed record FactInput(string Text, IReadOnlyList<string> Evidence);

/// <summary>An edge to create along with a new stub, from an existing concept to the stub.</summary>
public sealed record StubLink(string From, string EdgeType, IReadOnlyList<string> Evidence);

/// <summary>A note from the markdown prototype, imported as unverified content.</summary>
public sealed record LegacyNote(
    string Id, string Title, string Gloss, IReadOnlyList<string> Aliases, bool IsStub,
    string? Summary, string? Detail, IReadOnlyList<(string Type, string Target)> Edges);

/// <summary>
/// Command handlers. Each method runs in one unit of work: load aggregates, let them enforce
/// invariants and raise events, run policies, commit.
/// </summary>
public sealed class BLearnCommands(IStore store, IClock clock, ISourceFetcher fetcher)
{
    // ---------- Store ----------

    public CommandResult InitializeStore(string name, string rootTitle, string rootGloss, bool isPrivate, string? rootId = null) =>
        Execute(uow =>
        {
            var id = rootId ?? Ids.FromTitle(rootTitle);
            var settings = StoreSettings.Create(name, id, isPrivate);
            var root = Concept.CreateStub(id, rootTitle, rootGloss, [], uow.Reader);
            uow.Concepts.Save(root);
            uow.Settings.Save(settings);
        });

    public CommandResult RegisterSource(string name, string system, string scope, string minTrust = "captured") =>
        Execute(uow =>
        {
            var settings = uow.Settings.Load();
            settings.RegisterSource(name, system, scope, TrustNames.Parse(minTrust));
            uow.Settings.Save(settings);
        });

    public CommandResult SetPrivate(bool isPrivate) =>
        Execute(uow =>
        {
            var settings = uow.Settings.Load();
            settings.SetPrivate(isPrivate);
            uow.Settings.Save(settings);
        });

    // ---------- Concepts and edges ----------

    public CommandResult CreateStub(string? id, string title, string gloss, IReadOnlyList<string> aliases, StubLink? link = null) =>
        Execute(uow =>
        {
            var concept = Concept.CreateStub(id ?? Ids.FromTitle(title), title, gloss, aliases, uow.Reader);
            uow.Concepts.Save(concept);
            if (link is not null)
            {
                var graph = uow.Graph.Load();
                graph.AddEdge(link.From, link.EdgeType, concept.Id, link.Evidence);
                uow.Graph.Save(graph);
            }
        });

    public CommandResult Rename(string id, string title) => WithConcept(id, (c, uow) => c.Rename(title, uow.Reader));
    public CommandResult AddAlias(string id, string alias) => WithConcept(id, (c, uow) => c.AddAlias(alias, uow.Reader));
    public CommandResult ReviseGloss(string id, string gloss) => WithConcept(id, (c, _) => c.ReviseGloss(gloss));

    public CommandResult SubmitFacts(string id, IReadOnlyList<FactInput> facts) =>
        WithConcept(id, (c, uow) => c.SubmitFacts(facts.Select(f => new Fact(f.Text, f.Evidence)).ToList(), uow.Reader));

    public CommandResult SubmitTeaching(string id, string summary, string explanation) =>
        WithConcept(id, (c, uow) => c.SubmitTeaching(summary, explanation, uow.Reader));

    public CommandResult AddEdge(string source, string type, string target, IReadOnlyList<string> evidence) =>
        WithGraph(g => g.AddEdge(source, type, target, evidence));

    public CommandResult RemoveEdge(string source, string target) => WithGraph(g => g.RemoveEdge(source, target));

    public CommandResult AttachEdgeEvidence(string source, string target, IReadOnlyList<string> evidence) =>
        WithGraph(g => g.AttachEvidence(source, target, evidence));

    // ---------- Learner requests ----------

    public CommandResult RequestExpansion(string id) => WithConcept(id, (c, _) => c.RequestExpansion());
    public CommandResult RequestTeaching(string id, int? factIndex) => WithConcept(id, (c, _) => c.RequestTeaching(factIndex));
    public CommandResult AskQuestion(string id, string text) =>
        WithConcept(id, (c, _) => c.AskQuestion($"q-{clock.Now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}", text));
    public CommandResult RequestRecall(string id) => WithConcept(id, (c, _) => c.RequestRecall());

    // ---------- Evidence ----------

    /// <summary>An agent supplies a snapshot it retrieved through its own harness. Trust: captured.</summary>
    public CommandResult CaptureEvidence(
        string id, string source, string locator, string quote, string snapshotText, string mediaType, string session) =>
        Execute(uow =>
        {
            var hash = uow.Snapshots.Put(snapshotText, mediaType);
            var evidence = Evidence.Capture(id, source, locator, quote, snapshotText, hash, Trust.Captured, session,
                clock.Now, uow.Settings.Load(), uow.Reader);
            uow.Evidence.Save(evidence);
        });

    /// <summary>blearn fetches the source itself (web or local file). Trust: fetched.</summary>
    public async Task<CommandResult> FetchEvidenceAsync(string id, string source, string locator, string quote, string session)
    {
        FetchedSource fetched;
        try
        {
            var settings = store.Queries.GetInfo().Sources.FirstOrDefault(s => s.Name == source);
            if (settings is null)
                return CommandResult.Rejected("source-unregistered", $"Source '{source}' is not registered for this store.");
            if (settings.System is not ("web" or "file"))
                return CommandResult.Rejected("source-not-fetchable",
                    $"blearn can only fetch 'web' and 'file' sources itself. Use evidence capture for '{settings.System}'.");
            fetched = await fetcher.FetchAsync(locator);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException or ArgumentException or TaskCanceledException)
        {
            return CommandResult.Rejected("fetch-failed", $"Could not fetch '{locator}': {ex.Message}");
        }

        return Execute(uow =>
        {
            var hash = uow.Snapshots.Put(fetched.Content, fetched.MediaType);
            var evidence = Evidence.Capture(id, source, fetched.Locator, quote, fetched.Content, hash, Trust.Fetched, session,
                clock.Now, uow.Settings.Load(), uow.Reader);
            uow.Evidence.Save(evidence);
        });
    }

    public CommandResult ReconfirmEvidence(string id, string snapshotText, string mediaType, string session) =>
        Execute(uow =>
        {
            var evidence = uow.Evidence.Find(id) ?? throw new DomainViolation("evidence-missing", $"Evidence '{id}' does not exist.");
            var hash = uow.Snapshots.Put(snapshotText, mediaType);
            evidence.Reconfirm(snapshotText, hash, session);
            uow.Evidence.Save(evidence);
        });

    /// <summary>
    /// Re-checks evidence. Fetched evidence is fetched again; other evidence is re-checked against its stored
    /// snapshot (blearn cannot reach those systems).
    /// </summary>
    public async Task<CommandResult> VerifyEvidenceAsync(string id)
    {
        var view = store.Queries.GetEvidence(id);
        if (view is null) return CommandResult.Rejected("evidence-missing", $"Evidence '{id}' does not exist.");
        string content, mediaType;
        if (view.Trust == "fetched")
        {
            try
            {
                var fetched = await fetcher.FetchAsync(view.Locator);
                (content, mediaType) = (fetched.Content, fetched.MediaType);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                return CommandResult.Rejected("fetch-failed", $"Could not re-fetch '{view.Locator}': {ex.Message}");
            }
        }
        else
        {
            var snap = store.Queries.GetSnapshot(view.SnapshotHash)!;
            (content, mediaType) = (snap.Content, snap.MediaType);
        }

        return Execute(uow =>
        {
            var evidence = uow.Evidence.Find(id)!;
            evidence.Verify(content, uow.Snapshots.Put(content, mediaType));
            uow.Evidence.Save(evidence);
        });
    }

    // ---------- Learner ----------

    public CommandResult RecordVisit(string id, string? from, string? direction) => WithLearner(l => l.RecordVisit(id, from, direction));
    public CommandResult MarkKnown(string id) => WithLearner(l => l.MarkKnown(id));
    public CommandResult UnmarkKnown(string id) => WithLearner(l => l.UnmarkKnown(id));
    public CommandResult AnswerRecall(string id, string answer, string? feedback) => WithLearner(l => l.AnswerRecall(id, answer, feedback));

    // ---------- Import ----------

    /// <summary>
    /// Imports prototype notes in one transaction. Content arrives unverified: teaching is marked legacy,
    /// edges are marked legacy, and no facts are created because the notes cite no evidence.
    /// </summary>
    public CommandResult ImportLegacy(IReadOnlyList<LegacyNote> notes) =>
        Execute(uow =>
        {
            foreach (var n in notes)
                uow.Concepts.Save(Concept.CreateStub(n.Id, n.Title, n.Gloss, n.Aliases, uow.Reader));

            var graph = uow.Graph.Load();
            foreach (var n in notes)
                foreach (var (type, target) in n.Edges)
                {
                    graph.AddEdge(n.Id, type, target, [], legacy: true);
                    uow.Graph.Save(graph);
                    graph = uow.Graph.Load();
                }

            foreach (var n in notes.Where(n => !n.IsStub && n.Summary is not null))
            {
                var c = uow.Concepts.Find(n.Id)!;
                c.SubmitTeaching(n.Summary!, n.Detail ?? n.Summary!, uow.Reader, legacy: true);
                uow.Concepts.Save(c);
            }
        });

    // ---------- Plumbing ----------

    private CommandResult WithConcept(string id, Action<Concept, IUnitOfWork> act) =>
        Execute(uow =>
        {
            var c = uow.Concepts.Find(id) ?? throw new DomainViolation("concept-missing", $"Concept '{id}' does not exist.");
            act(c, uow);
            uow.Concepts.Save(c);
        });

    private CommandResult WithGraph(Action<Graph> act) =>
        Execute(uow =>
        {
            var g = uow.Graph.Load();
            act(g);
            uow.Graph.Save(g);
        });

    private CommandResult WithLearner(Action<Learner> act) =>
        Execute(uow =>
        {
            var l = uow.Learner.Load();
            act(l);
            uow.Learner.Save(l);
        });

    private CommandResult Execute(Action<IUnitOfWork> work)
    {
        try
        {
            using var uow = store.Begin();
            work(uow);
            Policies.React(uow.PendingEvents, uow);
            return CommandResult.Ok(uow.Commit());
        }
        catch (DomainViolation v)
        {
            return CommandResult.Rejected(v.Code, v.Message);
        }
        catch (ConcurrencyConflict c)
        {
            return CommandResult.Rejected("concurrency-conflict", c.Message);
        }
    }
}
