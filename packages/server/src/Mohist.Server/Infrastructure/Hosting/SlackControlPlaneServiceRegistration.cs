using Microsoft.Extensions.DependencyInjection.Extensions;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Infrastructure.Hosting;

/// <summary>
/// Registers the Slack control-plane application services that drive the
/// unified, resumable <c>setup</c> / <c>install-agent</c> progress flow.
/// The conventional <see cref="IScopedService"/> scan already registers
/// them as themselves; this explicit registration guarantees route
/// handlers resolve them by concrete type and collects the control-plane
/// wiring into a single seam so the CLI/adapter slice can depend on it.
/// The outbound Slack ports these services call are registered to
/// production adapters in the main composition (e.g.
/// <see cref="Mohist.Server.Infrastructure.Slack.Ports.SlackAppManagementPortAdapter"/>
/// for <see cref="ISlackAppManagementPort"/>); test hosts override the
/// port interfaces with fakes, which this registration does not disturb.
/// </summary>
public static class SlackControlPlaneServiceRegistration
{
    public static IServiceCollection AddSlackControlPlane(this IServiceCollection services)
    {
        services.TryAddScoped<SlackManagerSetupOrchestrator>();
        services.TryAddScoped<SlackInstallAgentService>();
        return services;
    }
}
