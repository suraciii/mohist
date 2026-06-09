using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Infrastructure.Hosting;

public static class MohistSiloRegistration
{
    public static ISiloBuilder ConfigureMohistSilo(this ISiloBuilder silo, IConfiguration configuration)
    {
        silo.UseLocalhostClustering();
        silo.UseAdoNetReminderService(options =>
        {
            options.Invariant = "System.Data.SQLite";
            options.ConnectionString = MohistServiceRegistration.ResolveSqliteConnectionString(configuration);
        });

        silo.ConfigureLogging(logging =>
        {
            logging.AddConsole();
        });

        silo.Services.AddScoped<IssueIdentityResolver>();
        silo.Services.AddTransient<WorktreeCleanupService>();
        silo.Services.AddTransient<IssueWorkflowCompletionHandler>();
        silo.Services.AddTransient<IssueWorkflowAbortedHandler>();
        silo.Services.AddTransient<AgentSessionRunnerBridge>();
        silo.Services.AddSingleton<IEventBus, InMemoryEventBus>();
        silo.Services.AddSingleton<ILogger<InMemoryEventBus>>(sp => sp.GetRequiredService<ILoggerFactory>().CreateLogger<InMemoryEventBus>());
        silo.Services.AddHostedService<EventHandlerRegistrationHostedService>();

        return silo;
    }
}
