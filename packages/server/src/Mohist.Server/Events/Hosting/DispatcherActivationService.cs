using Microsoft.Extensions.Hosting;
using Mohist.Server.Events.Grains;

namespace Mohist.Server.Events.Hosting;

public sealed class DispatcherActivationService : IHostedService
{
    private readonly IGrainFactory _grains;

    public DispatcherActivationService(IGrainFactory grains)
    {
        _grains = grains;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _grains
            .GetGrain<IDispatcherGrain>(DispatcherGrain.FixedKey)
            .EnsureStartedAsync();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
