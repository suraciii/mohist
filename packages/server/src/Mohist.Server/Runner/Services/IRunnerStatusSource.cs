namespace Mohist.Server.Runner.Services;

public interface IRunnerStatusSource
{
    Task<IReadOnlyList<RunnerStatusView>> GetOnlineRunnersAsync(string projectId, CancellationToken ct = default);
}
