using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.SpecTests.Support;

public static class TestServices
{
    public static IBackgroundTaskLauncher BackgroundTasks { get; } = new BackgroundTaskLauncher();

    public static IServiceCollection AddRequiredInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IBackgroundTaskLauncher, BackgroundTaskLauncher>();
        services.AddSingleton<IEventPushQueue>(NullEventPushQueue.Instance);
        services.AddSingleton<IAgentJobDispatchObserver>(NoopAgentJobDispatchObserver.Instance);
        return services;
    }
}
