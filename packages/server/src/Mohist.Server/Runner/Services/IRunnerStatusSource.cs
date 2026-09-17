namespace Mohist.Server.Runner.Services;

/// <summary>
/// Canonical read-only source for global Runner status. Consumers compute
/// availability from one snapshot so a single request never re-reads the
/// owner ledgers per Agent.
/// </summary>
public interface IRunnerStatusSource
{
    Task<RunnerStatusListSnapshot> GetGlobalRunnersAsync(CancellationToken ct = default);
}
