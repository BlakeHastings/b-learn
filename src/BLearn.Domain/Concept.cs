namespace BLearn.Domain;

/// <summary>An atomic claim with the evidence that supports it.</summary>
public sealed record Fact(string Text, IReadOnlyList<string> EvidenceIds);

/// <summary>A quiz question that tests one or more of the concept's facts (0-based indexes).</summary>
public sealed record RecallQuestion(string Prompt, string Answer, IReadOnlyList<int> FactIndexes);

public enum Verdict
{
    /// <summary>The cited quotes support the whole fact.</summary>
    Supports,

    /// <summary>The quotes support part of the fact; the note says which part is unsupported.</summary>
    Partial,

    /// <summary>The quotes do not support the fact.</summary>
    Unsupported,
}

/// <summary>A checker's verdict on one fact.</summary>
public sealed record FactCheck(int FactIndex, Verdict Verdict, string? Note);

/// <summary>A passage in the gloss or teaching that the checker found unsupported by the facts.</summary>
public sealed record TeachingFlag(string Excerpt, string Note);

/// <summary>The latest independent review of a concept's facts and teaching.</summary>
public sealed record CheckRecord(IReadOnlyList<FactCheck> Facts, IReadOnlyList<TeachingFlag> Flags, string Session, DateTimeOffset At);

public enum ConceptStatus
{
    /// <summary>Title and gloss only; not researched.</summary>
    Stub,

    /// <summary>Has teaching text but no evidence-backed facts (for example, imported legacy notes).</summary>
    Unverified,

    /// <summary>Has evidence-backed facts but no teaching text yet.</summary>
    Researched,

    /// <summary>Has evidence-backed facts and teaching text.</summary>
    Complete,
}

/// <summary>A concept atom: its names, gloss, facts and teaching. Edges belong to <see cref="Graph"/>.</summary>
public sealed class Concept : AggregateRoot
{
    private List<string> _aliases = [];
    private List<Fact> _facts = [];
    private List<RecallQuestion> _recall = [];

    public string Id { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Gloss { get; private set; } = "";
    public IReadOnlyList<string> Aliases => _aliases;
    public IReadOnlyList<Fact> Facts => _facts;
    public string? Summary { get; private set; }
    public string? Explanation { get; private set; }

    /// <summary>True when the teaching text was imported rather than written from facts.</summary>
    public bool TeachingLegacy { get; private set; }

    public IReadOnlyList<RecallQuestion> RecallQuestions => _recall;

    /// <summary>The latest checker review, or null. Cleared when the facts change.</summary>
    public CheckRecord? Check { get; private set; }

    public ConceptStatus Status => (_facts.Count > 0, Summary is not null) switch
    {
        (false, false) => ConceptStatus.Stub,
        (false, true) => ConceptStatus.Unverified,
        (true, false) => ConceptStatus.Researched,
        (true, true) => ConceptStatus.Complete,
    };

    private Concept() { }

    public static Concept Rehydrate(
        string id, string title, string gloss, IEnumerable<string> aliases, IEnumerable<Fact> facts,
        string? summary, string? explanation, bool teachingLegacy, long version,
        IEnumerable<RecallQuestion>? recall = null, CheckRecord? check = null) =>
        new()
        {
            Id = id,
            Title = title,
            Gloss = gloss,
            _aliases = aliases.ToList(),
            _facts = facts.ToList(),
            Summary = summary,
            Explanation = explanation,
            TeachingLegacy = teachingLegacy,
            Version = version,
            _recall = recall?.ToList() ?? [],
            Check = check,
        };

    public static Concept CreateStub(string id, string title, string gloss, IEnumerable<string> aliases, IGraphReader graph)
    {
        Ids.Require(id, "Concept id");
        title = title.Trim();
        if (title.Length == 0) throw new DomainViolation("title-required", "Title is required.");
        gloss = BLearn.Domain.Gloss.Require(gloss);
        if (graph.ConceptExists(id)) throw new DomainViolation("id-duplicate", $"Concept '{id}' already exists.");

        var aliasList = aliases.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct().ToList();
        var claimed = new Dictionary<string, string>();
        foreach (var name in aliasList.Prepend(title).Prepend(id))
        {
            var key = Names.Normalize(name);
            if (claimed.ContainsKey(key)) continue;
            var owner = graph.NameOwner(key);
            if (owner is not null)
                throw new DomainViolation("name-duplicate", $"Name '{name}' already belongs to concept '{owner}'. Reuse it instead of creating a duplicate.");
            claimed[key] = name;
        }

        var concept = new Concept { Id = id, Title = title, Gloss = gloss, _aliases = aliasList };
        concept.Raise(new ConceptStubCreated(id, title, gloss, aliasList));
        return concept;
    }

    public void Rename(string newTitle, IGraphReader graph)
    {
        newTitle = newTitle.Trim();
        if (newTitle.Length == 0) throw new DomainViolation("title-required", "Title is required.");
        var owner = graph.NameOwner(Names.Normalize(newTitle));
        if (owner is not null && owner != Id)
            throw new DomainViolation("name-duplicate", $"Name '{newTitle}' already belongs to concept '{owner}'.");
        var old = Title;
        Title = newTitle;
        Raise(new ConceptRenamed(Id, old, newTitle));
    }

    public void AddAlias(string alias, IGraphReader graph)
    {
        alias = alias.Trim();
        if (alias.Length == 0) throw new DomainViolation("alias-required", "Alias is required.");
        var owner = graph.NameOwner(Names.Normalize(alias));
        if (owner == Id) return;
        if (owner is not null)
            throw new DomainViolation("name-duplicate", $"Name '{alias}' already belongs to concept '{owner}'.");
        _aliases.Add(alias);
        Raise(new AliasAdded(Id, alias));
    }

    public void ChangeGloss(string gloss)
    {
        Gloss = BLearn.Domain.Gloss.Require(gloss);
        DropTeachingFlags();
        Raise(new GlossChanged(Id, Gloss));
    }

    /// <summary>Replaces the concept's facts. Every fact must cite existing evidence.</summary>
    public void ReplaceFacts(IReadOnlyList<Fact> facts, IGraphReader graph)
    {
        if (facts.Count == 0) throw new DomainViolation("facts-required", "Give at least one fact.");
        var clean = new List<Fact>();
        for (var i = 0; i < facts.Count; i++)
        {
            var f = facts[i];
            var text = f.Text.Trim();
            if (text.Length == 0) throw new DomainViolation("fact-format", $"Fact {i + 1} is empty.");
            if (text.Contains("\n\n")) throw new DomainViolation("fact-format", $"Fact {i + 1} must be a single paragraph. Split it into atomic facts.");
            var evidence = f.EvidenceIds.Select(e => e.Trim()).Where(e => e.Length > 0).Distinct().ToList();
            if (evidence.Count == 0) throw new DomainViolation("fact-uncited", $"Fact {i + 1} cites no evidence. Every fact needs a source.");
            foreach (var e in evidence)
                if (!graph.EvidenceExists(e))
                    throw new DomainViolation("evidence-missing", $"Fact {i + 1} cites unknown evidence '{e}'. Capture it first.");
            CheckLinks(text, graph, $"Fact {i + 1}");
            clean.Add(new Fact(text, evidence));
        }
        _facts = clean;
        // Verdicts and quiz questions point at facts by index, so they no longer apply.
        Check = null;
        _recall = [];
        Raise(new FactsReplaced(Id, clean));
    }

    /// <summary>Sets the teaching text. Unless importing legacy content, facts must exist first.</summary>
    public void ReplaceTeaching(string summary, string explanation, IGraphReader graph, bool legacy = false)
    {
        summary = summary.Trim();
        explanation = explanation.Trim();
        if (!legacy && _facts.Count == 0)
            throw new DomainViolation("teaching-before-facts", "Teaching is written from facts. Put evidence-backed facts first.");
        if (summary.Length == 0) throw new DomainViolation("teaching-format", "Summary is required.");
        if (explanation.Length == 0) throw new DomainViolation("teaching-format", "Explanation is required.");
        foreach (var (name, text) in new[] { ("Summary", summary), ("Explanation", explanation) })
        {
            if (Wikilinks.Lines(text).Any(l => !l.InFence && l.Line.StartsWith("## ")))
                throw new DomainViolation("teaching-format", $"{name} must not contain '## ' headings; sections are fixed.");
            CheckLinks(text, graph, name);
        }
        Summary = summary;
        Explanation = explanation;
        TeachingLegacy = legacy;
        DropTeachingFlags();
        Raise(new TeachingReplaced(Id, summary, explanation, legacy));
    }

    public void RequestExpansion()
    {
        if (Status is not (ConceptStatus.Stub or ConceptStatus.Unverified))
            throw new DomainViolation("already-researched", $"'{Id}' already has evidence-backed facts.");
        Raise(new ExpansionRequested(Id));
    }

    public void RequestTeaching(int? factIndex)
    {
        if (factIndex is { } i && (i < 0 || i >= _facts.Count))
            throw new DomainViolation("fact-index", $"'{Id}' has no fact {i}.");
        Raise(new TeachingRequested(Id, factIndex));
    }

    public void RequestRecall()
    {
        if (Status == ConceptStatus.Stub)
            throw new DomainViolation("recall-on-stub", $"'{Id}' has nothing to recall yet.");
        Raise(new RecallRequested(Id));
    }

    /// <summary>Replaces the quiz. Every question tests at least one existing fact.</summary>
    public void ReplaceRecallQuestions(IReadOnlyList<RecallQuestion> questions)
    {
        if (_facts.Count == 0) throw new DomainViolation("recall-on-stub", $"'{Id}' has no facts to test yet.");
        if (questions.Count == 0) throw new DomainViolation("recall-required", "Give at least one question.");
        var clean = new List<RecallQuestion>();
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            var prompt = q.Prompt.Trim();
            var answer = q.Answer.Trim();
            if (prompt.Length == 0 || answer.Length == 0)
                throw new DomainViolation("recall-format", $"Question {i + 1} needs a prompt and an answer.");
            var facts = q.FactIndexes.Distinct().ToList();
            if (facts.Count == 0) throw new DomainViolation("recall-uncited", $"Question {i + 1} tests no fact. List the fact indexes it tests.");
            foreach (var f in facts)
                if (f < 0 || f >= _facts.Count)
                    throw new DomainViolation("fact-index", $"Question {i + 1} tests fact {f}, but '{Id}' has facts 0 to {_facts.Count - 1}.");
            clean.Add(new RecallQuestion(prompt, answer, facts));
        }
        _recall = clean;
        Raise(new RecallQuestionsReplaced(Id, clean.Count));
    }

    /// <summary>
    /// Records an independent review: a verdict for every fact, and flags on gloss or teaching passages
    /// the facts do not support. The checker must not be the session that captured the cited evidence.
    /// </summary>
    public void RecordCheck(IReadOnlyList<FactCheck> checks, IReadOnlyList<TeachingFlag> flags, string session, DateTimeOffset at, IGraphReader graph)
    {
        session = session.Trim();
        if (session.Length == 0) throw new DomainViolation("session-required", "The checking session id is required.");
        if (_facts.Count == 0) throw new DomainViolation("facts-required", $"'{Id}' has no facts to check.");

        var byIndex = new Dictionary<int, FactCheck>();
        foreach (var c in checks)
        {
            if (c.FactIndex < 0 || c.FactIndex >= _facts.Count)
                throw new DomainViolation("fact-index", $"'{Id}' has facts 0 to {_facts.Count - 1}; there is no fact {c.FactIndex}.");
            if (c.Verdict != Verdict.Supports && string.IsNullOrWhiteSpace(c.Note))
                throw new DomainViolation("check-note-required", $"Fact {c.FactIndex} is {c.Verdict.ToString().ToLowerInvariant()}: say what the quotes do not support.");
            if (!byIndex.TryAdd(c.FactIndex, c with { Note = string.IsNullOrWhiteSpace(c.Note) ? null : c.Note.Trim() }))
                throw new DomainViolation("check-duplicate", $"Fact {c.FactIndex} has more than one verdict.");
        }
        var missing = Enumerable.Range(0, _facts.Count).Where(i => !byIndex.ContainsKey(i)).ToList();
        if (missing.Count > 0)
            throw new DomainViolation("check-incomplete", $"Give a verdict for every fact. Missing: {string.Join(", ", missing)}.");

        foreach (var evidence in _facts.SelectMany(f => f.EvidenceIds).Distinct())
            if (graph.EvidenceCapturedBy(evidence) == session)
                throw new DomainViolation("check-same-session",
                    $"Session '{session}' captured evidence '{evidence}' for these facts. The check must come from a different session.");

        var teaching = string.Join("\n", new[] { Gloss, Summary ?? "", Explanation ?? "" });
        var cleanFlags = new List<TeachingFlag>();
        foreach (var f in flags)
        {
            var excerpt = f.Excerpt.Trim();
            var note = f.Note.Trim();
            if (excerpt.Length == 0 || note.Length == 0) throw new DomainViolation("flag-format", "Each flag needs an excerpt and a note.");
            if (!QuoteMatcher.Contains(teaching, excerpt))
                throw new DomainViolation("flag-not-found", $"The excerpt '{excerpt}' does not appear in the gloss, summary or explanation. Quote it verbatim.");
            cleanFlags.Add(new TeachingFlag(excerpt, note));
        }

        var ordered = byIndex.OrderBy(k => k.Key).Select(k => k.Value).ToList();
        Check = new CheckRecord(ordered, cleanFlags, session, at);
        Raise(new FactsChecked(Id, session,
            ordered.Count(c => c.Verdict == Verdict.Supports), ordered.Count(c => c.Verdict == Verdict.Partial),
            ordered.Count(c => c.Verdict == Verdict.Unsupported), cleanFlags.Count));
    }

    /// <summary>Teaching flags quote the old text, so they lapse when that text changes.</summary>
    private void DropTeachingFlags()
    {
        if (Check is { Flags.Count: > 0 }) Check = Check with { Flags = [] };
    }

    private void CheckLinks(string text, IGraphReader graph, string where)
    {
        foreach (var link in Wikilinks.Scan(text))
        {
            if (link.Target == Id)
                throw new DomainViolation("link-self", $"{where} links to its own concept.");
            if (!graph.ConceptExists(link.Target))
                throw new DomainViolation("link-dangling", $"{where} links to unknown concept '{link.Target}'. Links use concept ids.");
            if (graph.EdgeBetween(Id, link.Target) is null)
                throw new DomainViolation("link-no-edge", $"{where} links to '{link.Target}' but no edge connects it to '{Id}'. Add the edge first so the link has a direction.");
        }
    }
}
