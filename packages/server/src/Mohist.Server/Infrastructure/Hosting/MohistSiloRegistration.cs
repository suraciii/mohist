using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Inbox;
using Mohist.Server.Notifications;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Orleans.Configuration;
using Orleans.Hosting;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Infrastructure.Hosting;

public static class MohistSiloRegistration
{
    public static ISiloBuilder ConfigureMohistSilo(this ISiloBuilder silo, IConfiguration configuration)
    {
        // Default to the well-known localhost clustering ports, but allow tests to
        // override them (via TestClusterPortAllocator) so multiple silos can coexist
        // in one process without fighting over 11111 / 30000. See design/testing.md
        // "并行与端口预算" and dotnet/orleans LocalhostSiloTests for the pattern.
        var siloPort = configuration.GetValue<int?>("Mohist:Silo:SiloPort") ?? EndpointOptions.DEFAULT_SILO_PORT;
        var gatewayPort = configuration.GetValue<int?>("Mohist:Silo:GatewayPort") ?? EndpointOptions.DEFAULT_GATEWAY_PORT;
        silo.UseLocalhostClustering(siloPort, gatewayPort);
        silo.AddActivityPropagation();
        silo.UseAdoNetReminderService(options =>
        {
            options.Invariant = "System.Data.SQLite";
            options.ConnectionString = MohistServiceRegistration.ResolveSqliteConnectionString(configuration);
        });

        silo.AddAdoNetGrainStorageAsDefault(options =>
        {
            options.Invariant = "System.Data.SQLite";
            options.ConnectionString = MohistServiceRegistration.ResolveSqliteConnectionString(configuration);
        });

        silo.ConfigureLogging(logging =>
        {
            logging.AddConsole();
        });

        // Orleans silo has its own DI container (separate from the web/api one).
        // Handlers are registered there too, since grains need them.
        silo.Services.AddCloudEventBus();
        silo.Services.AddSingleton<IEventStore, EventStore>();
        silo.Services.AddScoped<InboxStore>();
        silo.Services.AddScoped<IStateStore<DomainIssue>>(sp => sp.GetRequiredService<IIssueStore>());
        silo.Services.AddScoped<IIssueStore, IssueStore>();
        silo.Services.AddScoped<IWorkflowRunStore, WorkflowRunStore>();
        silo.Services.Configure<HermesNotificationOptions>(configuration.GetSection(HermesNotificationOptions.SectionName));
        silo.Services.AddSingleton<HermesIssueNotificationRenderer>();
        silo.Services.AddSingleton<IHermesIssueNotificationDispatcher, BackgroundHermesIssueNotificationDispatcher>();
        silo.Services.AddHttpClient<IHermesWebhookClient, HermesWebhookClient>();
        silo.Services.AddCloudEventHandlersFromAssembly(typeof(MohistSiloRegistration).Assembly);
        silo.Services.Configure<AgentJobOptions>(configuration.GetSection(AgentJobOptions.SectionName));
        silo.Services.TryAddSingleton(TimeProvider.System);

        return silo;
    }
}
