namespace Mohist.Server.Runner.Services;

public interface IRunnerStatusSource
{
    Task<RunnerAvailabilitySnapshot> GetAvailabilityAsync(CancellationToken ct = default);
}
