using Mohist.Server.Agent.Grains;
using Orleans;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Bounded convergence helpers for AgentJob dispatch state. They poll the
/// authoritative grain runtime snapshot instead of awaiting the
/// best-effort dispatch-observer side channel, which production replaces
/// with a NoOp and which can silently drop signals under load. The
/// snapshot reflects the ledger row, written before the observer fires,
/// so it is a strictly stronger signal. <paramref name="advance"/>
/// defaults to <c>job.CheckTimeoutsAsync()</c>: that drives retry
/// admission (and readiness-timeout evaluation) within the probe budget
/// without moving the fake clock, so the total advance can never cross
/// the dispatch retry bound.
/// </summary>
public static class AgentJobConvergence
{
    public static Task<AgentJobRuntimeSnapshot> WaitForAssignmentPreparedAsync(
        IAgentJobGrain job,
        TimeSpan? timeout = null,
        Func<Task>? advance = null) =>
        TestWait.ForAsync(
            () => job.GetRuntimeSnapshotAsync(),
            snapshot => !string.IsNullOrWhiteSpace(snapshot.RunnerId) || IsConverged(snapshot.Status),
            timeout ?? TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            $"AgentJob '{job.GetPrimaryKeyString()}' assignment prepared (RunnerId set) or terminal",
            advance ?? (() => job.CheckTimeoutsAsync()));

    public static Task<AgentJobRuntimeSnapshot> WaitForRunnerAcceptedAsync(
        IAgentJobGrain job,
        TimeSpan? timeout = null,
        Func<Task>? advance = null) =>
        TestWait.ForAsync(
            () => job.GetRuntimeSnapshotAsync(),
            snapshot => snapshot.RunnerAccepted || IsConverged(snapshot.Status),
            timeout ?? TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            $"AgentJob '{job.GetPrimaryKeyString()}' runner accepted or terminal",
            advance ?? (() => job.CheckTimeoutsAsync()));

    public static bool IsConverged(AgentJobStatus status) =>
        status is AgentJobStatus.Completed
            or AgentJobStatus.Failed
            or AgentJobStatus.Cancelled
            or AgentJobStatus.Unknown;
}
