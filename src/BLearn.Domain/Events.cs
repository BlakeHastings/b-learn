namespace BLearn.Domain;

/// <summary>Something that happened. <see cref="Stream"/> names the aggregate instance it belongs to.</summary>
public abstract record DomainEvent
{
    public abstract string Stream { get; }

    /// <summary>Event type name as stored and shown, for example "ConceptStubCreated".</summary>
    public string Type => GetType().Name;
}

// Store settings
public sealed record StoreCreated(string Name, string Root, bool Private) : DomainEvent { public override string Stream => "store"; }
public sealed record SourceRegistered(string Name, string System, string Scope, string MinTrust) : DomainEvent { public override string Stream => "store"; }
public sealed record StorePrivacySet(bool Private) : DomainEvent { public override string Stream => "store"; }

// Graph structure
public sealed record ConceptStubCreated(string ConceptId, string Title, string Gloss, IReadOnlyList<string> Aliases) : DomainEvent { public override string Stream => $"concept:{ConceptId}"; }
public sealed record ConceptRenamed(string ConceptId, string OldTitle, string NewTitle) : DomainEvent { public override string Stream => $"concept:{ConceptId}"; }
public sealed record AliasAdded(string ConceptId, string Alias) : DomainEvent { public override string Stream => $"concept:{ConceptId}"; }
public sealed record EdgeAdded(string Source, string EdgeType, string Target, IReadOnlyList<string> EvidenceIds, bool Legacy) : DomainEvent { public override string Stream => $"concept:{Source}"; }
public sealed record EdgeRemoved(string Source, string Target) : DomainEvent { public override string Stream => $"concept:{Source}"; }
public sealed record EdgeEvidenceAttached(string Source, string Target, IReadOnlyList<string> EvidenceIds) : DomainEvent { public override string Stream => $"concept:{Source}"; }

// Concept content
public sealed record FactsSubmitted(string ConceptId, IReadOnlyList<Fact> Facts) : DomainEvent { public override string Stream => $"concept:{ConceptId}"; }
public sealed record TeachingSubmitted(string ConceptId, string Summary, string Explanation, bool Legacy) : DomainEvent { public override string Stream => $"concept:{ConceptId}"; }
public sealed record GlossRevised(string ConceptId, string Gloss) : DomainEvent { public override string Stream => $"concept:{ConceptId}"; }

// Learner requests (the UI emits these; policies queue work for agents)
public sealed record ExpansionRequested(string ConceptId) : DomainEvent { public override string Stream => $"concept:{ConceptId}"; }
public sealed record TeachingRequested(string ConceptId, int? FactIndex) : DomainEvent { public override string Stream => $"concept:{ConceptId}"; }
public sealed record QuestionAsked(string ConceptId, string QuestionId, string Text) : DomainEvent { public override string Stream => $"concept:{ConceptId}"; }
public sealed record RecallRequested(string ConceptId) : DomainEvent { public override string Stream => $"concept:{ConceptId}"; }

// Evidence
public sealed record EvidenceCaptured(string EvidenceId, string Source, string Locator, string Quote, string SnapshotHash, string Trust, string CapturedBy) : DomainEvent { public override string Stream => $"evidence:{EvidenceId}"; }
public sealed record EvidenceVerified(string EvidenceId, string SnapshotHash) : DomainEvent { public override string Stream => $"evidence:{EvidenceId}"; }
public sealed record EvidenceReconfirmed(string EvidenceId, string SnapshotHash, string ReconfirmedBy) : DomainEvent { public override string Stream => $"evidence:{EvidenceId}"; }
public sealed record EvidenceMarkedStale(string EvidenceId, string SnapshotHash) : DomainEvent { public override string Stream => $"evidence:{EvidenceId}"; }

// Learner
public sealed record ConceptVisited(string ConceptId, string? FromConceptId, string? Direction) : DomainEvent { public override string Stream => "learner"; }
public sealed record ConceptMarkedKnown(string ConceptId) : DomainEvent { public override string Stream => "learner"; }
public sealed record ConceptUnmarkedKnown(string ConceptId) : DomainEvent { public override string Stream => "learner"; }
public sealed record RecallAnswered(string ConceptId, string Answer, string? Feedback) : DomainEvent { public override string Stream => "learner"; }
