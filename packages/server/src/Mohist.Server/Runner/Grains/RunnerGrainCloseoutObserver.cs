namespace Mohist.Server.Runner.Grains;

public interface IRunnerGrainCloseoutObserver
{
    Task AgentJobCloseoutStartingAsync(string runnerId, string agentJobId, string workId);
}

public sealed class NoopRunnerGrainCloseoutObserver : IRunnerGrainCloseoutObserver
{
    public static NoopRunnerGrainCloseoutObserver Instance { get; } = new();

    private NoopRunnerGrainCloseoutObserver()
    {
    }

    public Task AgentJobCloseoutStartingAsync(string runnerId, string agentJobId, string workId) => Task.CompletedTask;
}
