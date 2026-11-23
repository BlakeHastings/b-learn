using System.Text.Json;
using BLearn.Application;

namespace BLearn.Infrastructure.Agents;

/// <summary>
/// Connects the user's agent to a store without any pasting: claims one message at a time and runs the
/// agent harness on it with a fresh context, then marks the message done or hands it back.
/// While idle it waits on the queue, which keeps it shown as connected.
/// </summary>
public sealed class AgentRunner : IAsyncDisposable
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);

    private readonly IStore _store;
    private readonly IAgentProcess _process;
    private readonly string _exe;
    private readonly string _workRoot;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public AgentRunner(IStore store, IAgentProcess process, string harness, string exe, string workRoot)
    {
        _store = store;
        _process = process;
        Harness = harness;
        _exe = exe;
        _workRoot = workRoot;
        Session = $"{harness}-{Guid.NewGuid().ToString("N")[..6]}";
    }

    public string Harness { get; }
    public string Session { get; }
    public bool Running => _loop is { IsCompleted: false };

    public void Start()
    {
        if (_loop is not null) return;
        _store.Agents.Heartbeat(Session, Harness, managed: true, activity: "Connected");
        _loop = Task.Run(() => LoopAsync(_stop.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            QueueMessage? message;
            try { message = _store.Queue.TryReceive([], Lease, Session); }
            catch (Exception ex) when (ex is IOException or System.Data.Common.DbException) { message = null; }

            if (message is null)
            {
                try { await Task.Delay(Poll, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            await RunOneAsync(message, ct);
        }
    }

    private async Task RunOneAsync(QueueMessage message, CancellationToken ct)
    {
        var work = Directory.CreateDirectory(Path.Combine(_workRoot, $"task-{message.Id}")).FullName;
        var last = DateTimeOffset.MinValue;
        void Report(string activity)
        {
            // Throttled: the status line does not need every tool call.
            if (DateTimeOffset.Now - last < TimeSpan.FromSeconds(1)) return;
            last = DateTimeOffset.Now;
            try { _store.Agents.Heartbeat(Session, activity: activity); } catch (Exception ex) when (ex is IOException or System.Data.Common.DbException) { }
        }

        Report("Starting");
        // Report in while the agent thinks, so a crashed run is noticed within minutes, not at lease expiry.
        using var alive = new Timer(_ => { try { _store.Agents.Heartbeat(Session); } catch (Exception ex) when (ex is IOException or System.Data.Common.DbException) { } },
            null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        AgentRunResult result;
        var environment = new Dictionary<string, string> { ["BLEARN_STORE"] = _store.Path, ["BLEARN_SESSION"] = Session };
        try { result = await _process.RunAsync(Prompt(message), work, environment, Report, ct); }
        catch (OperationCanceledException) { _store.Queue.Nack(message.Receipt!, "Agent disconnected before finishing."); return; }
        catch (Exception ex) { result = new AgentRunResult(false, ex.Message); }

        // The agent saying it succeeded is not enough: check that the work actually landed in the store.
        var missing = result.Succeeded ? MissingOutcome(message) : null;
        if (result.Succeeded && missing is null) _store.Queue.Ack(message.Receipt!);
        else _store.Queue.Nack(message.Receipt!, missing ?? result.Error ?? "The agent did not finish.");
        _store.Agents.Heartbeat(Session, activity: result.Succeeded && missing is null ? "Finished" : "Could not finish the last task");
        KeepTranscript(work, message.Id);
    }

    /// <summary>What the task should have produced but did not, or null when its outcome is in the store.</summary>
    internal string? MissingOutcome(QueueMessage message)
    {
        using var doc = JsonDocument.Parse(message.PayloadJson);
        var p = doc.RootElement;
        string? Str(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var conceptId = Str("conceptId");
        var concept = conceptId is null ? null : _store.Queries.GetConcept(conceptId);
        return message.Queue switch
        {
            "ask" when Str("conversationId") is { } id && _store.Queries.GetConversation(id) is { AwaitingReply: true } =>
                "The agent finished without replying to the question.",
            "expand" when concept is { Status: "stub" } => "The agent finished without researching the concept.",
            "recall" when concept is { RecallQuestions.Count: 0 } => "The agent finished without saving quiz questions.",
            "check" when concept is { Check: null, Facts.Count: > 0 } => "The agent finished without saving a check.",
            _ => null,
        };
    }

    /// <summary>Keeps the agent's transcript for troubleshooting (the last 50 tasks) and removes the rest of the work folder.</summary>
    private void KeepTranscript(string work, long messageId)
    {
        try
        {
            var logs = Directory.CreateDirectory(Path.Combine(_workRoot, "logs")).FullName;
            var transcript = Path.Combine(work, "transcript.jsonl");
            if (File.Exists(transcript)) File.Copy(transcript, Path.Combine(logs, $"task-{messageId}.jsonl"), overwrite: true);
            foreach (var old in new DirectoryInfo(logs).GetFiles("task-*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Skip(50)) old.Delete();
            Directory.Delete(work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Where transcripts of finished tasks are kept.</summary>
    public string LogDirectory => Path.Combine(_workRoot, "logs");

    private string Prompt(QueueMessage message)
    {
        var payload = JsonSerializer.Serialize(JsonDocument.Parse(message.PayloadJson).RootElement, new JsonSerializerOptions { WriteIndented = true });
        return $"""
            Do this b-learn task, then stop.

            Task kind: {message.Queue}
            {payload}

            {AgentBrief.Render(_store.Queries.GetInfo(), "blearn", managedSession: Session)}
            """;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
        try { _store.Agents.Leave(Session); } catch (Exception ex) when (ex is IOException or System.Data.Common.DbException or ObjectDisposedException) { }
        _stop.Dispose();
    }
}
