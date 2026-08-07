using Orleans;
using Orleans.Core.Internal;
using Orleans.Runtime;

namespace Mohist.Server.TestSupport;

public static class TestLifecycle
{
    public static async Task Deactivate(this IAddressable grain) =>
        await grain.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

    public static async Task DeactivateAndWait(this IAddressable grain, IGrainFactory grains)
    {
        var grainId = grain.GetGrainId();
        await grain.Deactivate();

        var management = grains.GetGrain<IManagementGrain>(0);
        await TestWait.ForAsync(
            () => management.GetDetailedGrainStatistics(),
            activations => activations.All(stat => stat.GrainId != grainId),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(25),
            $"Grain '{grainId}' to deactivate");
    }
}
