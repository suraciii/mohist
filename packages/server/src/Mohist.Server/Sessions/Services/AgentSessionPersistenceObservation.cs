namespace Mohist.Server.Sessions.Services;

public enum AgentSessionPersistenceOutcome
{
    Succeeded,
    TranscriptFailed,
    StateFailed,
}

public readonly record struct AgentSessionPersistenceResult(
    string SessionId,
    long CycleId,
    AgentSessionPersistenceOutcome Outcome);

public interface IAgentSessionPersistenceObserver
{
    long StartCycle(string sessionId);
    void Report(AgentSessionPersistenceResult result);
}

public sealed class NoopAgentSessionPersistenceObserver : IAgentSessionPersistenceObserver
{
    public static readonly NoopAgentSessionPersistenceObserver Instance = new();

    private NoopAgentSessionPersistenceObserver()
    {
    }

    public long StartCycle(string sessionId) => 0;

    public void Report(AgentSessionPersistenceResult result)
    {
    }
}
