using Mohist.Server.TestSupport;
using Mohist.Server.Runner.Grains;
using Orleans;

namespace Mohist.Server.SpecTests.Support;

public static class AgentJobDispatchTestAccess
{
    /// <summary>
    /// Releases one AgentJob dispatch backoff retry. Orleans resolves grain
    /// timers from the DI <see cref="System.TimeProvider"/>, so a job whose
    /// first dispatch attempt found no runner stays Pending forever while the
    /// fake clock is frozen. The trailing grain call orders the released timer
    /// callback ahead of the caller's next probe. Callers must keep the total
    /// advance under <c>DispatchRetryBound</c>, or the job gives up as
    /// RunnerUnavailable instead of dispatching.
    /// </summary>
    public static Task ReleaseDispatchBackoffAsync(this MohistIntegrationFixture fixture)
    {
        fixture.TimeProvider.Advance(TimeSpan.FromMilliseconds(250));
        return fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global).ListRunnerIdsAsync();
    }

    /// <summary>
    /// Renders every runner currently known to the shared registry with the
    /// work each one holds. AgentJob dispatch selects across all online
    /// runners in the silo, not just the caller's, so a dispatch that never
    /// reaches the expected runner is only diagnosable against this snapshot.
    /// </summary>
    public static async Task<string> DescribeRunnerRegistryAsync(this MohistIntegrationFixture fixture)
    {
        var registry = fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var lines = new List<string>();
        foreach (var id in await registry.ListRunnerIdsAsync())
        {
            var state = await fixture.Grains.GetGrain<IRunnerGrain>(id).GetRuntimeStateAsync();
            var works = state.ActiveWorks is { Count: > 0 } active
                ? string.Join(", ", active.Select(work => $"{work.WorkId}<-{work.OwnerId}"))
                : "none";
            lines.Add($"  {id} status={state.Status} works=[{works}]");
        }
        return lines.Count == 0 ? "  (no runners registered)" : string.Join('\n', lines);
    }
}
