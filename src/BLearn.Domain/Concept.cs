namespace BLearn.Domain;

/// <summary>An atomic claim with the evidence that supports it.</summary>
public sealed record Fact(string Text, IReadOnlyList<string> EvidenceIds);

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

    public string Id { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Gloss { get; private set; } = "";
    public IReadOnlyList<string> Aliases => _aliases;
    public IReadOnlyList<Fact> Facts => _facts;
    public string? Summary { get; private set; }
    public string? Explanation { get; private set; }

    /// <summary>True when the teaching text was imported rather than written from facts.</summary>
    public bool TeachingLegacy { get; private set; }

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
        string? summary, string? explanation, bool teachingLegacy, long version) =>
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

    public void ReviseGloss(string gloss)
    {
        Gloss = BLearn.Domain.Gloss.Require(gloss);
        Raise(new GlossRevised(Id, Gloss));
    }

    /// <summary>Replaces the concept's facts. Every fact must cite existing evidence.</summary>
    public void SubmitFacts(IReadOnlyList<Fact> facts, IGraphReader graph)
    {
        if (facts.Count == 0) throw new DomainViolation("facts-required", "Submit at least one fact.");
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
        Raise(new FactsSubmitted(Id, clean));
    }

    /// <summary>Sets the teaching text. Unless importing legacy content, facts must exist first.</summary>
    public void SubmitTeaching(string summary, string explanation, IGraphReader graph, bool legacy = false)
    {
        summary = summary.Trim();
        explanation = explanation.Trim();
        if (!legacy && _facts.Count == 0)
            throw new DomainViolation("teaching-before-facts", "Teaching is written from facts. Submit evidence-backed facts first.");
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
        Raise(new TeachingSubmitted(Id, summary, explanation, legacy));
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

    public void AskQuestion(string questionId, string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new DomainViolation("question-required", "Question text is required.");
        Raise(new QuestionAsked(Id, questionId, text));
    }

    public void RequestRecall()
    {
        if (Status == ConceptStatus.Stub)
            throw new DomainViolation("recall-on-stub", $"'{Id}' has nothing to recall yet.");
        Raise(new RecallRequested(Id));
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
