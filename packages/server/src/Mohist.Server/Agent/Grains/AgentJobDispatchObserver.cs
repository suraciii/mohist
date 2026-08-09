namespace Mohist.Server.Agent.Grains;

public interface IAgentJobDispatchObserver
{
    Task AssignmentPreparedAsync(string agentJobId, string runnerId, string workId);

    Task AssignmentPreparedBoundaryAsync(string agentJobId, string runnerId, string workId);

    Task AssignmentReadyForPollAsync(string agentJobId, string runnerId, string workId);

    Task RunnerAcceptedAsync(string agentJobId, string runnerId, string workId);
}

public sealed class NoopAgentJobDispatchObserver : IAgentJobDispatchObserver
{
    public static NoopAgentJobDispatchObserver Instance { get; } = new();

    private NoopAgentJobDispatchObserver()
    {
    }

    public Task AssignmentPreparedAsync(string agentJobId, string runnerId, string workId) => Task.CompletedTask;

    public Task AssignmentPreparedBoundaryAsync(string agentJobId, string runnerId, string workId) => Task.CompletedTask;

    public Task AssignmentReadyForPollAsync(string agentJobId, string runnerId, string workId) => Task.CompletedTask;

    public Task RunnerAcceptedAsync(string agentJobId, string runnerId, string workId) => Task.CompletedTask;
}
