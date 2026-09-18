using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Orleans.Runtime;
using Orleans.Storage;

namespace Mohist.Server.Runner.Services;

/// <summary>
/// Durable Runner lifecycle facts that are already persisted by
/// <see cref="RunnerGrain"/> and remain meaningful after a Server restart:
/// last known registration identity, the last presence touch, and the update
/// drain fence. They are reconstructed without activating the grain because
/// activation is allowed to reconcile pending closeout work.
/// </summary>
public sealed record RunnerDurableStatus(
    RunnerInfo? Info,
    DateTimeOffset? LastPresenceAt,
    string? UpdateInterruptId,
    RunnerEnvironmentApplicationObservation? EnvironmentApplication = null,
    RunnerEnvironmentObservationView? EnvironmentObservation = null);

public interface IRunnerDurableStatusReader
{
    Task<RunnerDurableStatus?> ReadAsync(string runnerId, CancellationToken ct = default);
}

/// <summary>
/// Reads <see cref="RunnerState"/> straight from the configured grain storage.
/// This never activates the Runner grain, so a status GET cannot trigger
/// closeout reconciliation or any other lifecycle mutation.
/// </summary>
public sealed class RunnerDurableStatusReader(IGrainStorage storage, IGrainFactory grains)
    : IRunnerDurableStatusReader, IScopedService
{
    public async Task<RunnerDurableStatus?> ReadAsync(string runnerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runnerId))
            return null;

        var grainState = new GrainState<RunnerState>();
        try
        {
            // Resolving the reference never activates the grain; only invoking
            // a grain method would.
            var grainId = grains.GetGrain<IRunnerGrain>(runnerId).GetGrainId();
            await storage.ReadStateAsync("runner", grainId, grainState);
        }
        catch
        {
            // A missing row is not a status failure: the Runner simply has no
            // durable facts yet, the same as before it first registered.
            return null;
        }

        if (!grainState.RecordExists || grainState.State is null)
            return null;

        return new RunnerDurableStatus(
            grainState.State.LastKnownInfo,
            grainState.State.LastPresenceAt,
            grainState.State.UpdateInterruptFence?.PendingId,
            RunnerEnvironmentApplicationObservationMapper.From(
                grainState.State.EnvironmentApplication),
            RunnerEnvironmentObservationMapper.From(
                grainState.State.EnvironmentObservation));
    }
}
