namespace BLearn.Application;

/// <summary>
/// Which agents are connected to a store. Agents report in by waiting for work (lease post), by
/// finishing or releasing work, and by running commands with their session id. Presence is
/// operational state, not domain history, so it is not written to the event log.
/// </summary>
public interface IAgentPresence
{
    /// <summary>Records that a session is alive. Non-null arguments update what is known about it.</summary>
    void Heartbeat(string session, string? harness = null, bool? managed = null, string? activity = null);

    /// <summary>Records that a session is waiting for work on these queues (all queues when empty).</summary>
    void Listening(string session, IReadOnlyCollection<string> queues);

    /// <summary>Marks a session as gone, for agents that disconnect cleanly.</summary>
    void Leave(string session);

    AgentsStatus Status();
}

/// <summary>What one agent is doing: listening for work, working on a message, or offline.</summary>
public sealed record AgentView(
    string Session, string? Harness, bool Managed, string State, DateTimeOffset LastSeen, string? Activity, AgentWork? Work);

public sealed record AgentWork(long MessageId, string Queue, string? ConceptId, string? Text);

/// <param name="Connected">True when at least one agent is listening or working.</param>
public sealed record AgentsStatus(bool Connected, IReadOnlyList<AgentView> Agents);

public static class AgentStates
{
    public const string Listening = "listening";
    public const string Working = "working";
    public const string Offline = "offline";

    /// <summary>A waiting agent polls several times a second; silence this long means it stopped waiting.</summary>
    public static readonly TimeSpan ListeningWindow = TimeSpan.FromSeconds(20);

    /// <summary>A working agent may think or browse for a while between commands.</summary>
    public static readonly TimeSpan WorkingWindow = TimeSpan.FromMinutes(15);
}
