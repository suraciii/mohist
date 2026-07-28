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
    void Report(AgentSessionPersistenceResult result);
}

public sealed class NoopAgentSessionPersistenceObserver : IAgentSessionPersistenceObserver
{
    public static readonly NoopAgentSessionPersistenceObserver Instance = new();

    private NoopAgentSessionPersistenceObserver()
    {
    }

    public void Report(AgentSessionPersistenceResult result)
    {
    }
}
