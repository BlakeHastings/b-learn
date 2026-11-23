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
        public static readonly string[] All = [Expand, Teach, Ask, Recall];
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
                    uow.Enqueue(Work(Queues.Ask, $"ask:{r.QuestionId}",
                        new { kind = "ask", conceptId = r.ConceptId, questionId = r.QuestionId, text = r.Text }));
                    break;
                case RecallRequested r:
                    uow.Enqueue(Work(Queues.Recall, $"recall:{r.ConceptId}", new { kind = "recall", conceptId = r.ConceptId }));
                    break;
            }
        }
    }

    private static WorkItem Work(string queue, string key, object payload) =>
        new(queue, key, JsonSerializer.Serialize(payload, Json.Options));
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}
