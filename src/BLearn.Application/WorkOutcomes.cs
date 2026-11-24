using System.Text.Json;

namespace BLearn.Application;

/// <summary>
/// Deterministic check that a piece of work produced its result before it is marked done. An agent saying it
/// finished is not proof: the reply, research, quiz or check has to be in the store.
/// </summary>
public static class WorkOutcomes
{
    /// <summary>What the message should have produced but did not, or null when its result is in the store.</summary>
    public static string? Missing(IQueries queries, QueueMessage message)
    {
        using var doc = JsonDocument.Parse(message.PayloadJson);
        var p = doc.RootElement;
        string? Str(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var conceptId = Str("conceptId");
        var concept = conceptId is null ? null : queries.GetConcept(conceptId);
        return message.Queue switch
        {
            Policies.Queues.Ask when Str("conversationId") is { } id && queries.GetConversation(id) is { AwaitingReply: true } =>
                "The question has no reply yet. Post one with reply post.",
            Policies.Queues.Expand when concept is { Status: "stub" } => "The concept is still a stub. Put facts first.",
            Policies.Queues.Recall when concept is { RecallQuestions.Count: 0 } => "The concept has no quiz questions. Put them with recall-question put.",
            Policies.Queues.Check when concept is { Check: null, Facts.Count: > 0 } => "The concept has no check. Put one with check put.",
            _ => null,
        };
    }

    /// <summary>
    /// Completes a leased message after checking its outcome. With <paramref name="withoutOutcome"/> the agent
    /// states why it finished without a result (for example, no source in scope); the reason is recorded.
    /// </summary>
    public static CommandResult Complete(IStore store, string receipt, string? withoutOutcome)
    {
        var message = store.Queue.Find(receipt);
        if (message is not { State: "leased" })
            return CommandResult.Rejected("lease-lost", "Unknown receipt, or the lease expired and the message was redelivered.");
        if (string.IsNullOrWhiteSpace(withoutOutcome) && Missing(store.Queries, message) is { } missing)
            return CommandResult.Rejected("outcome-missing",
                $"{missing} If there is honestly nothing to produce (for example no source in scope), finish with --without-outcome \"why\".");
        return store.Queue.Ack(receipt, withoutOutcome?.Trim())
            ? CommandResult.Ok([])
            : CommandResult.Rejected("lease-lost", "Unknown receipt, or the lease expired and the message was redelivered.");
    }
}
