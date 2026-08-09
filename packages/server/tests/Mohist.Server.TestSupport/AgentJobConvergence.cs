namespace Mohist.Server.TestSupport;

/// <summary>
/// Test-only adapters for fixture-owned dispatch signals. These helpers keep
/// the protocol assertions on the real HTTP boundary while ordering setup
/// and post-claim checks through the observer probe instead of polling grain
/// runtime snapshots.
/// </summary>
public static class AgentJobConvergence
{
    public static Task WaitForAssignmentPreparedAsync(
        AgentJobDispatchProbe probe,
        string agentJobId,
        TimeSpan timeout,
        Action<TimeSpan> advance,
        CancellationToken cancellationToken = default) =>
        probe.WaitForAssignmentPreparedAsync(
            agentJobId,
            timeout,
            advance,
            cancellationToken);

    public static Task<DispatchRunnerAccepted> WaitForRunnerAcceptedAsync(
        AgentJobDispatchProbe probe,
        string agentJobId,
        TimeSpan timeout,
        Action<TimeSpan> advance,
        CancellationToken cancellationToken = default) =>
        probe.WaitForRunnerAcceptedAsync(
            agentJobId,
            timeout,
            advance,
            cancellationToken);
}
