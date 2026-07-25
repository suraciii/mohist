using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Mohist.Server.Events.Grains;
using Mohist.Server.Otel;
using Orleans.Configuration;
using Orleans.Hosting;

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
        silo.AddIncomingGrainCallFilter<RequestWorkIncomingGrainCallFilter>();
        silo.AddOutgoingGrainCallFilter<RequestWorkOutgoingGrainCallFilter>();
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

        // Issue-362: the dispatcher grain registers a ~1s reminder, well
        // below the runtime's default MinimumReminderPeriod (~1 minute).
        // Lower the floor to 100ms so a fast cadence is accepted at
        // registration time; the grain's EventDispatcherOptions still
        // decides the actual cadence.
        silo.Configure<ReminderOptions>(options =>
        {
            options.MinimumReminderPeriod = TimeSpan.FromMilliseconds(100);
        });

        // Issue-362 (T-002): the cluster-singleton EventDispatcherGrain
        // resolves EventDispatcherOptions from its constructor. Options
        // binding happens in the silo DI scope so the reminder cadence
        // configured under "EventDispatcher" reaches the grain regardless
        // of whether the host DI is populated.
        silo.Services.Configure<EventDispatcherOptions>(
            configuration.GetSection(EventDispatcherOptions.SectionName));

        return silo;
    }
}
