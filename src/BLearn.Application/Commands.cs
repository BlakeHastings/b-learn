using BLearn.Domain;

namespace BLearn.Application;

/// <summary>The outcome of a command: the events it emitted, or the invariant it broke.</summary>
public sealed record CommandResult(bool Accepted, IReadOnlyList<StoredEvent> Events, string? Violation, string? Message)
{
    public static CommandResult Ok(IReadOnlyList<StoredEvent> events) => new(true, events, null, null);
    public static CommandResult Rejected(string code, string message) => new(false, [], code, message);
}

public sealed record FactInput(string Text, IReadOnlyList<string> Evidence);

/// <summary>An edge to create along with a new concept, from an existing concept to the new one.</summary>
public sealed record ConceptLink(string From, string EdgeType, IReadOnlyList<string> Evidence);

/// <summary>A new concept. It starts as a stub (title and gloss only).</summary>
public sealed record ConceptInput(string? Id, string Title, string Gloss, IReadOnlyList<string>? Aliases = null, ConceptLink? Link = null);

/// <summary>The fields a concept PATCH may change. Null or empty means unchanged.</summary>
public sealed record ConceptPatch(string? Title = null, string? Gloss = null, IReadOnlyList<string>? AddAliases = null);

/// <summary>A learner request on a concept. Kind is expand, teach or recall; questions are their own resource.</summary>
public sealed record RequestInput(string Kind, int? FactIndex = null, string? Text = null);

/// <summary>
/// New evidence. Give <see cref="SnapshotText"/> (with <see cref="Locator"/>) for content the caller retrieved
/// (trust captured), or <see cref="Url"/> for blearn to fetch itself (trust fetched).
/// </summary>
public sealed record EvidenceInput(
    string Id, string Source, string Quote, string Session,
    string? Locator = null, string? SnapshotText = null, string? MediaType = null, string? Url = null);

/// <summary>
/// A learner question. Without <see cref="ConversationId"/> it starts a conversation about the concept,
/// optionally anchored to a highlighted passage or one fact (0-based); with it, it follows up.
/// </summary>
public sealed record QuestionInput(string ConceptId, string Text, string? Quote = null, int? FactIndex = null, string? ConversationId = null);

public sealed record RecallQuestionInput(string Prompt, string Answer, IReadOnlyList<int>? Facts);

public sealed record FactCheckInput(int Index, string Verdict, string? Note);

public sealed record TeachingFlagInput(string Excerpt, string Note);

/// <summary>A checker review: a verdict for every fact, and any unsupported passages in gloss or teaching.</summary>
public sealed record CheckInput(IReadOnlyList<FactCheckInput>? Facts, IReadOnlyList<TeachingFlagInput>? Teaching);

/// <summary>A note from the markdown prototype, imported as unverified content.</summary>
public sealed record LegacyNote(
    string Id, string Title, string Gloss, IReadOnlyList<string> Aliases, bool IsStub,
    string? Summary, string? Detail, IReadOnlyList<(string Type, string Target)> Edges);

/// <summary>
/// Command handlers, named for the resource they change and the HTTP verb that changes it. Each method
/// runs in one unit of work: load aggregates, let them enforce invariants and raise events, run policies, commit.
/// </summary>
public sealed class BLearnCommands(IStore store, IClock clock, ISourceFetcher fetcher)
{
    // ---------- Store ----------

    /// <summary>POST /stores: initializes an empty store file with its settings and root concept.</summary>
    public CommandResult PostStore(string name, string rootTitle, string rootGloss, bool isPrivate, string? rootId = null) =>
        Execute(uow =>
        {
            var id = rootId ?? Ids.FromTitle(rootTitle);
            var settings = StoreSettings.Create(name, id, isPrivate);
            var root = Concept.CreateStub(id, rootTitle, rootGloss, [], uow.Reader);
            uow.Concepts.Save(root);
            uow.Settings.Save(settings);
        });

    /// <summary>PATCH /store.</summary>
    public CommandResult PatchStore(bool? isPrivate) =>
        Execute(uow =>
        {
            if (isPrivate is null) throw new DomainViolation("patch-empty", "Nothing to change. Give private.");
            var settings = uow.Settings.Load();
            settings.SetPrivate(isPrivate.Value);
            uow.Settings.Save(settings);
        });

    /// <summary>POST /sources.</summary>
    public CommandResult PostSource(string name, string system, string scope, string minTrust = "captured") =>
        Execute(uow =>
        {
            var settings = uow.Settings.Load();
            settings.RegisterSource(name, system, scope, TrustNames.Parse(minTrust));
            uow.Settings.Save(settings);
        });

    // ---------- Concepts ----------

    /// <summary>POST /concepts: a new stub, optionally linked from an existing concept in the same transaction.</summary>
    public CommandResult PostConcept(ConceptInput input) =>
        Execute(uow =>
        {
            var concept = Concept.CreateStub(input.Id ?? Ids.FromTitle(input.Title), input.Title, input.Gloss, input.Aliases ?? [], uow.Reader);
            uow.Concepts.Save(concept);
            if (input.Link is { } link)
            {
                var graph = uow.Graph.Load();
                graph.AddEdge(link.From, link.EdgeType, concept.Id, link.Evidence);
                uow.Graph.Save(graph);
            }
        });

    /// <summary>PATCH /concepts/{id}: title, gloss and aliases only. All changes commit together or not at all.</summary>
    public CommandResult PatchConcept(string id, ConceptPatch patch) =>
        WithConcept(id, (c, uow) =>
        {
            var aliases = patch.AddAliases ?? [];
            if (patch.Title is null && patch.Gloss is null && aliases.Count == 0)
                throw new DomainViolation("patch-empty", "Nothing to change. Give title, gloss or aliases to add.");
            if (patch.Title is not null && patch.Title.Trim() != c.Title) c.Rename(patch.Title, uow.Reader);
            if (patch.Gloss is not null && patch.Gloss.Trim() != c.Gloss) c.ChangeGloss(patch.Gloss);
            foreach (var alias in aliases) c.AddAlias(alias, uow.Reader);
        });

    /// <summary>PUT /concepts/{id}/facts: replaces every fact.</summary>
    public CommandResult PutFacts(string id, IReadOnlyList<FactInput> facts) =>
        WithConcept(id, (c, uow) => c.ReplaceFacts(facts.Select(f => new Fact(f.Text, f.Evidence ?? [])).ToList(), uow.Reader));

    /// <summary>PUT /concepts/{id}/teaching: replaces the summary and explanation.</summary>
    public CommandResult PutTeaching(string id, string summary, string explanation) =>
        WithConcept(id, (c, uow) => c.ReplaceTeaching(summary, explanation, uow.Reader));

    /// <summary>POST /concepts/{id}/requests: the learner asks for work, which policies queue for agents.</summary>
    public CommandResult PostRequest(string id, RequestInput request) =>
        WithConcept(id, (c, _) =>
        {
            switch (request.Kind)
            {
                case "expand": c.RequestExpansion(); break;
                case "teach": c.RequestTeaching(request.FactIndex); break;
                case "recall": c.RequestRecall(); break;
                case "ask": throw new DomainViolation("kind-invalid", "Questions are their own resource: POST /questions (blearn question post).");
                default: throw new DomainViolation("kind-invalid", "Kind must be expand, teach or recall.");
            }
        });

    /// <summary>PUT /concepts/{id}/recall-questions: replaces the quiz for a concept.</summary>
    public CommandResult PutRecallQuestions(string id, IReadOnlyList<RecallQuestionInput> questions) =>
        WithConcept(id, (c, _) => c.ReplaceRecallQuestions(questions.Select(q => new RecallQuestion(q.Prompt ?? "", q.Answer ?? "", q.Facts ?? [])).ToList()));

    /// <summary>PUT /concepts/{id}/check: records an independent review of the concept's facts and teaching.</summary>
    public CommandResult PutCheck(string id, CheckInput input, string session) =>
        WithConcept(id, (c, uow) =>
        {
            var checks = (input.Facts ?? []).Select(f => new FactCheck(f.Index,
                Enum.TryParse<Verdict>(f.Verdict, ignoreCase: true, out var v) && !int.TryParse(f.Verdict, out _)
                    ? v
                    : throw new DomainViolation("verdict-invalid", $"Fact {f.Index}: verdict must be supports, partial or unsupported."),
                f.Note)).ToList();
            var flags = (input.Teaching ?? []).Select(t => new TeachingFlag(t.Excerpt ?? "", t.Note ?? "")).ToList();
            c.RecordCheck(checks, flags, session, clock.Now, uow.Reader);
        });

    // ---------- Conversations ----------

    /// <summary>POST /questions: the learner asks, starting a conversation or following up in one.</summary>
    public CommandResult PostQuestion(QuestionInput input) =>
        Execute(uow =>
        {
            if (input.ConversationId is { } existingId)
            {
                var conversation = uow.Conversations.Find(existingId)
                    ?? throw new DomainViolation("conversation-missing", $"Conversation '{existingId}' does not exist.");
                conversation.Ask(input.Text, clock.Now);
                uow.Conversations.Save(conversation);
                return;
            }
            var concept = uow.Concepts.Find(input.ConceptId)
                ?? throw new DomainViolation("concept-missing", $"Concept '{input.ConceptId}' does not exist.");
            var id = $"conv-{clock.Now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
            var started = Conversation.Start(id, concept.Id, input.Text, input.Quote, input.FactIndex, concept.Facts.Count, clock.Now, uow.Reader);
            uow.Conversations.Save(started);
        });

    /// <summary>POST /conversations/{id}/replies: an agent answers the latest question.</summary>
    public CommandResult PostReply(string conversationId, string text, string session) =>
        Execute(uow =>
        {
            var conversation = uow.Conversations.Find(conversationId)
                ?? throw new DomainViolation("conversation-missing", $"Conversation '{conversationId}' does not exist.");
            conversation.Reply(text, session, clock.Now, uow.Reader);
            uow.Conversations.Save(conversation);
        });

    // ---------- Edges ----------

    /// <summary>POST /edges.</summary>
    public CommandResult PostEdge(string source, string type, string target, IReadOnlyList<string> evidence) =>
        WithGraph(g => g.AddEdge(source, type, target, evidence));

    /// <summary>DELETE /edges/{from}/{to}.</summary>
    public CommandResult DeleteEdge(string source, string target) => WithGraph(g => g.RemoveEdge(source, target));

    /// <summary>POST /edges/{from}/{to}/evidence: cites evidence for an edge, which verifies a legacy edge.</summary>
    public CommandResult PostEdgeEvidence(string source, string target, IReadOnlyList<string> evidence) =>
        WithGraph(g => g.AttachEvidence(source, target, evidence));

    // ---------- Evidence ----------

    /// <summary>POST /evidence: from a snapshot the caller retrieved (captured), or a URL blearn fetches (fetched).</summary>
    public async Task<CommandResult> PostEvidenceAsync(EvidenceInput input)
    {
        if ((input.Url is null) == (input.SnapshotText is null))
            return CommandResult.Rejected("evidence-input", "Give either a snapshot (with a locator) or a url for blearn to fetch, not both.");

        if (input.SnapshotText is { } snapshot)
            return Execute(uow =>
            {
                var hash = uow.Snapshots.Put(snapshot, input.MediaType ?? "text/plain");
                var evidence = Evidence.Capture(input.Id, input.Source, input.Locator ?? "", input.Quote, snapshot, hash, Trust.Captured,
                    input.Session, clock.Now, uow.Settings.Load(), uow.Reader);
                uow.Evidence.Save(evidence);
            });

        FetchedSource fetched;
        try
        {
            var source = store.Queries.GetInfo().Sources.FirstOrDefault(s => s.Name == input.Source);
            if (source is null)
                return CommandResult.Rejected("source-unregistered", $"Source '{input.Source}' is not registered for this store.");
            if (source.System is not ("web" or "file"))
                return CommandResult.Rejected("source-not-fetchable",
                    $"blearn can only fetch 'web' and 'file' sources itself. Post a snapshot for '{source.System}'.");
            fetched = await fetcher.FetchAsync(input.Url!);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException or ArgumentException or TaskCanceledException)
        {
            return CommandResult.Rejected("fetch-failed", $"Could not fetch '{input.Url}': {ex.Message}");
        }

        return Execute(uow =>
        {
            var hash = uow.Snapshots.Put(fetched.Content, fetched.MediaType);
            var evidence = Evidence.Capture(input.Id, input.Source, fetched.Locator, input.Quote, fetched.Content, hash, Trust.Fetched,
                input.Session, clock.Now, uow.Settings.Load(), uow.Reader);
            uow.Evidence.Save(evidence);
        });
    }

    /// <summary>DELETE /evidence/{id}: only evidence that nothing cites.</summary>
    public CommandResult DeleteEvidence(string id) =>
        Execute(uow =>
        {
            var evidence = uow.Evidence.Find(id) ?? throw new DomainViolation("evidence-missing", $"Evidence '{id}' does not exist.");
            evidence.Remove(uow.Reader);
            uow.Evidence.Save(evidence);
        });

    /// <summary>POST /evidence/{id}/confirmations: a different session confirms the quote from its own retrieval.</summary>
    public CommandResult PostConfirmation(string id, string snapshotText, string mediaType, string session) =>
        Execute(uow =>
        {
            var evidence = uow.Evidence.Find(id) ?? throw new DomainViolation("evidence-missing", $"Evidence '{id}' does not exist.");
            var hash = uow.Snapshots.Put(snapshotText, mediaType);
            evidence.Reconfirm(snapshotText, hash, session);
            uow.Evidence.Save(evidence);
        });

    /// <summary>
    /// POST /evidence/{id}/verifications: re-checks evidence. Fetched evidence is fetched again; other evidence
    /// is re-checked against its stored snapshot (blearn cannot reach those systems).
    /// </summary>
    public async Task<CommandResult> PostVerificationAsync(string id)
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

    /// <summary>POST /learner/visits.</summary>
    public CommandResult PostVisit(string id, string? from, string? direction) => WithLearner(l => l.RecordVisit(id, from, direction));

    /// <summary>PUT /learner/known/{id}.</summary>
    public CommandResult PutKnown(string id) => WithLearner(l => l.MarkKnown(id));

    /// <summary>DELETE /learner/known/{id}.</summary>
    public CommandResult DeleteKnown(string id) => WithLearner(l => l.UnmarkKnown(id));

    /// <summary>POST /learner/recall-answers: the learner's answer to a quiz question, optionally self-graded.</summary>
    public CommandResult PostRecallAnswer(string id, string answer, string? feedback, int? questionIndex = null, bool? correct = null) =>
        WithLearner(l => l.AnswerRecall(id, answer, feedback, questionIndex, correct));

    // ---------- Import ----------

    /// <summary>
    /// POST /imports: imports prototype notes in one transaction. Content arrives unverified: teaching is marked
    /// legacy, edges are marked legacy, and no facts are created because the notes cite no evidence.
    /// </summary>
    public CommandResult PostImport(IReadOnlyList<LegacyNote> notes) =>
        Execute(uow =>
        {
            foreach (var n in notes)
            {
                // A store's root is created with the store; adopt it when the import contains the same id.
                if (uow.Concepts.Find(n.Id) is { Status: ConceptStatus.Stub } existing)
                {
                    if (existing.Title != n.Title) existing.Rename(n.Title, uow.Reader);
                    if (existing.Gloss != n.Gloss) existing.ChangeGloss(n.Gloss);
                    foreach (var alias in n.Aliases) existing.AddAlias(alias, uow.Reader);
                    uow.Concepts.Save(existing);
                    continue;
                }
                uow.Concepts.Save(Concept.CreateStub(n.Id, n.Title, n.Gloss, n.Aliases, uow.Reader));
            }

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
                c.ReplaceTeaching(n.Summary!, n.Detail ?? n.Summary!, uow.Reader, legacy: true);
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
