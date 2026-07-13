using Microsoft.Extensions.Hosting;
using Mohist.Server.Events.Grains;

namespace Mohist.Server.Events.Hosting;

/// <summary>
/// Activates the cluster-singleton <see cref="EventDispatcherGrain"/>
/// on host start so its persistent Orleans reminder begins firing even
/// without any external poke. The grain is keyed by
/// <see cref="EventDispatcherGrain.Global"/>; activation under any
/// other key is silently ignored.
/// </summary>
public sealed class DispatcherActivationService : IHostedService
{
    private readonly IGrainFactory _grains;

    public DispatcherActivationService(IGrainFactory grains)
    {
        _grains = grains;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _grains
            .GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global)
            .DispatchNowAsync();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
