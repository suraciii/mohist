using System.Collections.Concurrent;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Runner.Services;

public sealed record RunnerStatusObservation(
    RunnerStatus Status,
    DateTimeOffset LastPresenceAt,
    RunnerInfo? Info,
    bool Draining,
    string? UpdateInterruptId,
    RunnerDispatchObservation? DispatchObservation,
    RunnerEnvironmentApplicationObservation? EnvironmentApplication = null,
    RunnerEnvironmentObservationView? EnvironmentObservation = null);

/// <summary>
/// Process-local read projection for ephemeral Runner lifecycle facts. Status
/// reads this projection instead of activating RunnerGrain, whose activation
/// is allowed to reconcile durable closeout work.
/// </summary>
public sealed class RunnerStatusObservationStore : ISingletonService
{
    private readonly ConcurrentDictionary<string, RunnerStatusObservation> _observations =
        new(StringComparer.Ordinal);

    public RunnerStatusObservation? Get(string runnerId) =>
        _observations.TryGetValue(runnerId, out var observation) ? observation : null;

    public void Set(string runnerId, RunnerStatusObservation observation) =>
        _observations[runnerId] = observation;

    public void Remove(string runnerId) => _observations.TryRemove(runnerId, out _);

    public void Clear() => _observations.Clear();
}
