using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Inbox;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Orleans.Hosting;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Infrastructure.Hosting;

public static class MohistSiloRegistration
{
    public static ISiloBuilder ConfigureMohistSilo(this ISiloBuilder silo, IConfiguration configuration)
    {
        silo.UseLocalhostClustering();
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
        silo.Services.AddScoped<IEventStore, EventStore>();
        silo.Services.AddScoped<InboxStore>();
        silo.Services.AddScoped<IStateStore<DomainIssue>, IssueStore>();
        silo.Services.AddScoped<IWorkflowRunStore, WorkflowRunStore>();
        silo.Services.AddCloudEventHandlersFromAssembly(typeof(MohistSiloRegistration).Assembly);
        silo.Services.Configure<AgentJobOptions>(configuration.GetSection(AgentJobOptions.SectionName));
        silo.Services.AddSingleton(TimeProvider.System);

        return silo;
    }
}
