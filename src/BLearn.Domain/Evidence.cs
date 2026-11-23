namespace BLearn.Domain;

/// <summary>How far a piece of evidence has been independently checked. Ordered weakest to strongest.</summary>
public enum Trust
{
    /// <summary>An agent submitted the snapshot from its own harness.</summary>
    Captured = 0,

    /// <summary>A different agent, in a separate session, re-fetched the source and the quote matched.</summary>
    Reconfirmed = 1,

    /// <summary>blearn fetched the source itself.</summary>
    Fetched = 2,
}

public static class TrustNames
{
    public static string ToName(this Trust t) => t.ToString().ToLowerInvariant();

    public static Trust Parse(string name) =>
        Enum.TryParse<Trust>(name, ignoreCase: true, out var t)
            ? t
            : throw new DomainViolation("trust-invalid", $"Unknown trust level '{name}'. Allowed: captured, reconfirmed, fetched.");
}

/// <summary>A verbatim quote from a registered source, with a snapshot of what was retrieved.</summary>
public sealed class Evidence : AggregateRoot
{
    public const int QuoteMaxLength = 1000;

    public string Id { get; private set; } = "";
    public string Source { get; private set; } = "";
    public string Locator { get; private set; } = "";
    public string Quote { get; private set; } = "";
    public string SnapshotHash { get; private set; } = "";
    public Trust Trust { get; private set; }
    public bool Stale { get; private set; }
    public string CapturedBy { get; private set; } = "";
    public string? ReconfirmedBy { get; private set; }
    public DateTimeOffset RetrievedAt { get; private set; }

    private Evidence() { }

    public static Evidence Rehydrate(
        string id, string source, string locator, string quote, string snapshotHash, Trust trust, bool stale,
        string capturedBy, string? reconfirmedBy, DateTimeOffset retrievedAt, long version) =>
        new()
        {
            Id = id, Source = source, Locator = locator, Quote = quote, SnapshotHash = snapshotHash, Trust = trust,
            Stale = stale, CapturedBy = capturedBy, ReconfirmedBy = reconfirmedBy, RetrievedAt = retrievedAt, Version = version,
        };

    /// <param name="snapshotText">Full text of what was retrieved; the quote must appear in it.</param>
    /// <param name="trust">Captured when an agent supplies the snapshot; Fetched only when blearn fetched it.</param>
    public static Evidence Capture(
        string id, string source, string locator, string quote, string snapshotText, string snapshotHash,
        Trust trust, string capturedBy, DateTimeOffset retrievedAt, StoreSettings settings, IGraphReader graph)
    {
        Ids.Require(id, "Evidence id");
        if (graph.EvidenceExists(id)) throw new DomainViolation("evidence-duplicate", $"Evidence '{id}' already exists.");
        if (settings.FindSource(source) is null)
            throw new DomainViolation("source-unregistered",
                $"Source '{source}' is not registered for this store. Registered: {string.Join(", ", settings.Sources.Select(s => s.Name))}.");
        locator = locator.Trim();
        if (locator.Length == 0) throw new DomainViolation("locator-required", "A locator (URL, file path, record id) is required.");
        quote = quote.Trim();
        if (quote.Length == 0) throw new DomainViolation("quote-required", "A verbatim quote is required.");
        if (quote.Length > QuoteMaxLength) throw new DomainViolation("quote-too-long", $"Quote is {quote.Length} chars; max is {QuoteMaxLength}. Quote only the supporting passage.");
        if (trust == Trust.Reconfirmed) throw new DomainViolation("trust-invalid", "New evidence cannot start as reconfirmed.");
        if (string.IsNullOrWhiteSpace(capturedBy)) throw new DomainViolation("session-required", "The capturing session id is required.");
        if (!QuoteMatcher.Contains(snapshotText, quote))
            throw new DomainViolation("quote-not-found", "The quote does not appear in the snapshot. Quote the source verbatim.");

        var e = new Evidence
        {
            Id = id, Source = source, Locator = locator, Quote = quote, SnapshotHash = snapshotHash,
            Trust = trust, CapturedBy = capturedBy.Trim(), RetrievedAt = retrievedAt,
        };
        e.Raise(new EvidenceCaptured(id, source, locator, quote, snapshotHash, trust.ToName(), e.CapturedBy));
        return e;
    }

    /// <summary>Re-checks the quote against a fresh snapshot. A missing quote marks the evidence stale.</summary>
    public void Verify(string snapshotText, string snapshotHash)
    {
        if (QuoteMatcher.Contains(snapshotText, Quote))
        {
            Stale = false;
            SnapshotHash = snapshotHash;
            Raise(new EvidenceVerified(Id, snapshotHash));
        }
        else
        {
            Stale = true;
            Raise(new EvidenceMarkedStale(Id, snapshotHash));
        }
    }

    /// <summary>True once <see cref="Remove"/> succeeded; the repository deletes the record.</summary>
    public bool Removed { get; private set; }

    /// <summary>Deletes evidence that nothing cites. Cited evidence stays, so facts and edges never lose their support.</summary>
    public void Remove(IGraphReader graph)
    {
        if (graph.EvidenceCited(Id))
            throw new DomainViolation("evidence-cited", $"Evidence '{Id}' is cited by a fact or edge. Remove those citations first.");
        Removed = true;
        Raise(new EvidenceRemoved(Id));
    }

    /// <summary>An independent session confirms the quote from its own retrieval.</summary>
    public void Reconfirm(string snapshotText, string snapshotHash, string session)
    {
        session = session.Trim();
        if (session.Length == 0) throw new DomainViolation("session-required", "The reconfirming session id is required.");
        if (session == CapturedBy)
            throw new DomainViolation("reconfirm-same-session", "Reconfirmation must come from a different session than the capture.");
        if (!QuoteMatcher.Contains(snapshotText, Quote))
            throw new DomainViolation("quote-not-found", "The quote does not appear in the new snapshot.");
        ReconfirmedBy = session;
        Stale = false;
        if (Trust < Trust.Reconfirmed) Trust = Trust.Reconfirmed;
        Raise(new EvidenceReconfirmed(Id, snapshotHash, session));
    }
}
