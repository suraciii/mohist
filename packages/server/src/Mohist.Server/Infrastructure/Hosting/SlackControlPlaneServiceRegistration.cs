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
/// The three outbound Slack ports they call remain <c>Unavailable</c> by
/// default and are overridden with fakes in tests
/// (<see cref="ISlackAppManagementPort"/> etc.), so existing fake
/// substitution is unchanged.
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
