using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Events;
using Orleans.Hosting;

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
        silo.Services.AddCloudEventHandlersFromAssembly(typeof(MohistSiloRegistration).Assembly);
        silo.Services.Configure<AgentJobOptions>(configuration.GetSection(AgentJobOptions.SectionName));

        return silo;
    }
}
