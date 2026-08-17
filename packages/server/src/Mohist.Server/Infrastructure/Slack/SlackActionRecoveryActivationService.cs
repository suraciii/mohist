using Microsoft.Extensions.Hosting;
using Mohist.Server.Infrastructure.Slack.Grains;
using Orleans;

namespace Mohist.Server.Infrastructure.Slack;

public sealed class SlackActionRecoveryActivationService : IHostedService
{
    private readonly IGrainFactory _grains;

    public SlackActionRecoveryActivationService(IGrainFactory grains) => _grains = grains;

    public Task StartAsync(CancellationToken cancellationToken) =>
        _grains.GetGrain<ISlackActionRecoveryGrain>(SlackActionRecoveryGrain.Global)
            .RecoverNowAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
