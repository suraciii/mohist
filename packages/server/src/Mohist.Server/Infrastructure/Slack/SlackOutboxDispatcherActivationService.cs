using Microsoft.Extensions.Hosting;
using Orleans;
using Mohist.Server.Infrastructure.Slack.Grains;

namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Activates the cluster-singleton
/// <see cref="SlackOutboxDispatcherGrain"/> on host start so its
/// persistent Orleans reminder begins firing even without any
/// external poke. Mirrors
/// <see cref="Mohist.Server.Events.Hosting.DispatcherActivationService"/>:
/// the grain is keyed by <see cref="SlackOutboxDispatcherGrain.Global"/>;
/// activation under any other key is silently ignored.
/// </summary>
public sealed class SlackOutboxDispatcherActivationService : IHostedService
{
    private readonly IGrainFactory _grains;

    public SlackOutboxDispatcherActivationService(IGrainFactory grains)
    {
        _grains = grains;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _grains
            .GetGrain<ISlackOutboxDispatcherGrain>(SlackOutboxDispatcherGrain.Global)
            .DispatchNowAsync();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
