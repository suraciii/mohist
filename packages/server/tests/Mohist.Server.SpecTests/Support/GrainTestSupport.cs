using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Shared cluster-lifecycle helpers for server specs. Wraps the
/// Orleans-wide <c>IManagementGrain.ForceActivationCollection</c> surface
/// so individual specs do not reach into the management grain directly
/// for what is, semantically, "rehydrate this grain from its store".
/// </summary>
public static class GrainTestSupport
{
    /// <summary>
    /// Forces every eligible activation in the test silo to deactivate
    /// immediately. Use this when a test needs a fresh activation that
    /// rehydrates from the persistent store (for example, after mutating
    /// the store outside the grain). The wait-for-drop loop and the
    /// grain-scoped gate are intentionally not in the helper — they vary
    /// per test and live next to their assertions.
    /// </summary>
    public static Task ForceActivationCollectionAsync(IGrainFactory grains) =>
        grains.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

    /// <summary>
    /// Forces every eligible activation to drop, then waits for a
    /// specific grain id to leave the activation set. The wait is
    /// bounded by <paramref name="timeout"/> / <paramref name="step"/>
    /// and polls the silo grain-statistics surface; the loop itself
    /// performs no wall-clock waiting.
    /// </summary>
    public static async Task ForceActivationCollectionForGrainAsync(
        IGrainFactory grains,
        string grainTypeMarker,
        string grainIdMarker,
        TimeSpan timeout,
        TimeSpan step,
        string description)
    {
        var management = grains.GetGrain<IManagementGrain>(0);
        await TestWait.ForAsync(
            async () => await management.GetDetailedGrainStatistics(),
            stats => !stats.Any(stat =>
                stat.GrainType.Contains(grainTypeMarker, StringComparison.Ordinal)
                && stat.GrainId.ToString()!.Contains(grainIdMarker, StringComparison.Ordinal)),
            timeout,
            step,
            description,
            () => management.ForceActivationCollection(TimeSpan.Zero));
    }
}
