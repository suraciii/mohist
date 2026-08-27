using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Slack.Services;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.TestSupport;

public static class TestServices
{
    public static IBackgroundTaskLauncher BackgroundTasks { get; } = new BackgroundTaskLauncher();

    public static IServiceCollection AddRequiredInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IBackgroundTaskLauncher, BackgroundTaskLauncher>();
        services.AddSingleton<IEventPushQueue>(NullEventPushQueue.Instance);
        services.AddSingleton<IAgentJobDispatchObserver>(NoopAgentJobDispatchObserver.Instance);
        services.AddSingleton<ReportPersistenceFailureProbe>();
        services.AddSingleton<IWorkflowReportPersistenceFailureInjector>(sp =>
            sp.GetRequiredService<ReportPersistenceFailureProbe>());
        services.AddSingleton<IAgentJobReportPersistenceFailureInjector>(sp =>
            sp.GetRequiredService<ReportPersistenceFailureProbe>());
        // AgentJobGrain revokes Manager execution leases in its recovery
        // transitions, so every test silo that can activate the grain needs
        // the same runtime-only singletons the production host registers.
        // The DbContextFactory is supplied by each silo configuration.
        services.AddSingleton<ManagerExecutionLeaseStore>(sp =>
            new ManagerExecutionLeaseStore(sp.GetRequiredService<IDbContextFactory<MohistDbContext>>()));
        services.AddSingleton<IManagerExecutionLeaseStore>(sp =>
            sp.GetRequiredService<ManagerExecutionLeaseStore>());
        services.AddSingleton<ManagerDeploymentEpoch>(sp =>
            new ManagerDeploymentEpoch(sp.GetRequiredService<IManagerExecutionLeaseStore>()));
        services.AddSingleton<IManagerDeploymentEpoch>(sp =>
            sp.GetRequiredService<ManagerDeploymentEpoch>());
        services.AddSingleton<ManagerExecutionCapabilityIssuer>();
        return services;
    }
}
