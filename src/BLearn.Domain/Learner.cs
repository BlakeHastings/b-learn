namespace BLearn.Domain;

/// <summary>The learner's history in a store: visits, known concepts, recall answers.</summary>
public sealed class Learner(IGraphReader graph) : AggregateRoot
{
    public void RecordVisit(string conceptId, string? fromConceptId, string? direction)
    {
        Require(conceptId);
        if (fromConceptId is not null) Require(fromConceptId);
        if (direction is not null && direction is not ("down" or "up" or "lateral"))
            throw new DomainViolation("direction-invalid", "Direction must be down, up or lateral.");
        Raise(new ConceptVisited(conceptId, fromConceptId, direction));
    }

    public void MarkKnown(string conceptId)
    {
        Require(conceptId);
        Raise(new ConceptMarkedKnown(conceptId));
    }

    public void UnmarkKnown(string conceptId)
    {
        Require(conceptId);
        Raise(new ConceptUnmarkedKnown(conceptId));
    }

    /// <summary>Records the learner's answer to a quiz question, with their self-assessment when given.</summary>
    public void AnswerRecall(string conceptId, string answer, string? feedback, int? questionIndex = null, bool? correct = null)
    {
        Require(conceptId);
        if (string.IsNullOrWhiteSpace(answer)) throw new DomainViolation("answer-required", "An answer is required.");
        if (questionIndex is < 0) throw new DomainViolation("question-index", "Question indexes start at 0.");
        Raise(new RecallAnswered(conceptId, questionIndex, correct, answer.Trim(), feedback?.Trim()));
    }

    private void Require(string id)
    {
        if (!graph.ConceptExists(id)) throw new DomainViolation("concept-missing", $"Concept '{id}' does not exist.");
    }
}
