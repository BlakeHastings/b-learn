namespace BLearn.Domain;

public enum MessageRole
{
    Learner,
    Agent,
}

/// <summary>One turn in a conversation.</summary>
public sealed record ConversationMessage(int Ordinal, MessageRole Role, string Text, string? Session, DateTimeOffset At);

/// <summary>
/// A learner's conversation with an agent about a concept, optionally anchored to a passage they
/// highlighted or to one fact. Each learner question waits for exactly one agent reply.
/// </summary>
public sealed class Conversation : AggregateRoot
{
    public const int QuoteMaxLength = 2000;
    public const int TextMaxLength = 20000;

    private List<ConversationMessage> _messages = [];

    public string Id { get; private set; } = "";
    public string ConceptId { get; private set; } = "";

    /// <summary>The passage the learner highlighted, if any.</summary>
    public string? Quote { get; private set; }

    /// <summary>The fact the conversation is about, if any (0-based).</summary>
    public int? FactIndex { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }
    public IReadOnlyList<ConversationMessage> Messages => _messages;

    /// <summary>True when the last message is a learner question without a reply.</summary>
    public bool AwaitingReply => _messages.Count > 0 && _messages[^1].Role == MessageRole.Learner;

    private Conversation() { }

    public static Conversation Rehydrate(
        string id, string conceptId, string? quote, int? factIndex, DateTimeOffset startedAt,
        IEnumerable<ConversationMessage> messages, long version) =>
        new()
        {
            Id = id, ConceptId = conceptId, Quote = quote, FactIndex = factIndex, StartedAt = startedAt,
            _messages = messages.OrderBy(m => m.Ordinal).ToList(), Version = version,
        };

    /// <param name="factCount">How many facts the concept has, to validate <paramref name="factIndex"/>.</param>
    public static Conversation Start(
        string id, string conceptId, string text, string? quote, int? factIndex, int factCount, DateTimeOffset at, IGraphReader graph)
    {
        Ids.Require(id, "Conversation id");
        if (!graph.ConceptExists(conceptId)) throw new DomainViolation("concept-missing", $"Concept '{conceptId}' does not exist.");
        quote = string.IsNullOrWhiteSpace(quote) ? null : quote.Trim();
        if (quote is { Length: > QuoteMaxLength })
            throw new DomainViolation("quote-too-long", $"The highlighted passage is {quote.Length} chars; max is {QuoteMaxLength}.");
        if (factIndex is { } i && (i < 0 || i >= factCount))
            throw new DomainViolation("fact-index", $"'{conceptId}' has no fact {i}. Fact indexes start at 0.");

        var c = new Conversation { Id = id, ConceptId = conceptId, Quote = quote, FactIndex = factIndex, StartedAt = at };
        c.Ask(text, at);
        return c;
    }

    /// <summary>The learner asks (or follows up). Rejected while the previous question has no reply.</summary>
    public void Ask(string text, DateTimeOffset at)
    {
        text = RequireText(text, "question-required", "Question text is required.");
        if (AwaitingReply)
            throw new DomainViolation("question-pending", "The previous question has no reply yet. Wait for it before asking again.");
        var m = new ConversationMessage(_messages.Count, MessageRole.Learner, text, null, at);
        _messages.Add(m);
        Raise(new QuestionAsked(ConceptId, Id, m.Ordinal, text, Quote, FactIndex));
    }

    /// <summary>An agent answers the latest question. Links in the reply must name existing concepts.</summary>
    public void Reply(string text, string session, DateTimeOffset at, IGraphReader graph)
    {
        text = RequireText(text, "reply-required", "Reply text is required.");
        session = session.Trim();
        if (session.Length == 0) throw new DomainViolation("session-required", "The replying session id is required.");
        if (!AwaitingReply)
            throw new DomainViolation("reply-unexpected", "Every question in this conversation already has a reply.");
        foreach (var link in Wikilinks.Scan(text))
            if (!graph.ConceptExists(link.Target))
                throw new DomainViolation("link-dangling", $"The reply links to unknown concept '{link.Target}'. Links use concept ids.");
        var m = new ConversationMessage(_messages.Count, MessageRole.Agent, text, session, at);
        _messages.Add(m);
        Raise(new ReplyPosted(ConceptId, Id, m.Ordinal, text, session));
    }

    private static string RequireText(string text, string code, string message)
    {
        text = text.Trim();
        if (text.Length == 0) throw new DomainViolation(code, message);
        if (text.Length > TextMaxLength) throw new DomainViolation("text-too-long", $"Text is {text.Length} chars; max is {TextMaxLength}.");
        return text;
    }
}
