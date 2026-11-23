using System.Text.Json;
using BLearn.Domain;

namespace BLearn.Application;

/// <summary>
/// Deterministic policies: "whenever event, then enqueue work". They run inside the command's
/// unit of work, so an accepted request and its queued work commit together.
/// </summary>
public static class Policies
{
    public static class Queues
    {
        public const string Expand = "expand";
        public const string Teach = "teach";
        public const string Ask = "ask";
        public const string Recall = "recall";
        public const string Check = "check";
        public static readonly string[] All = [Expand, Teach, Ask, Recall, Check];
    }

    public static void React(IReadOnlyList<DomainEvent> events, IUnitOfWork uow)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case ExpansionRequested r:
                    uow.Enqueue(Work(Queues.Expand, $"expand:{r.ConceptId}", new { kind = "expand", conceptId = r.ConceptId }));
                    break;
                case TeachingRequested r:
                    uow.Enqueue(Work(Queues.Teach, $"teach:{r.ConceptId}:{r.FactIndex?.ToString() ?? "all"}",
                        new { kind = "teach", conceptId = r.ConceptId, factIndex = r.FactIndex }));
                    break;
                case QuestionAsked r:
                    uow.Enqueue(Work(Queues.Ask, $"ask:{r.ConversationId}:{r.MessageOrdinal}",
                        new { kind = "ask", conceptId = r.ConceptId, conversationId = r.ConversationId, text = r.Text, quote = r.Quote, factIndex = r.FactIndex },
                        priority: 10));
                    break;
                case RecallRequested r:
                    uow.Enqueue(Work(Queues.Recall, $"recall:{r.ConceptId}", new { kind = "recall", conceptId = r.ConceptId }));
                    break;
                // New facts or teaching need an independent review. One open check per concept covers both.
                case FactsReplaced r:
                    uow.Enqueue(Work(Queues.Check, $"check:{r.ConceptId}", new { kind = "check", conceptId = r.ConceptId }, priority: -10));
                    break;
                case TeachingReplaced { Legacy: false } r:
                    uow.Enqueue(Work(Queues.Check, $"check:{r.ConceptId}", new { kind = "check", conceptId = r.ConceptId }, priority: -10));
                    break;
            }
        }
    }

    /// <summary>Questions jump the line (a learner is waiting); checks wait behind other work.</summary>
    private static WorkItem Work(string queue, string key, object payload, int priority = 0) =>
        new(queue, key, JsonSerializer.Serialize(payload, Json.Options), priority);
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}
