using Microsoft.Extensions.Configuration;
using Mohist.Server.Events.Grains;
using Mohist.Server.Otel;
using Orleans.Configuration;
using Orleans.Hosting;
using System.Reflection;

namespace Mohist.Server.Infrastructure.Hosting;

public static class MohistSiloRegistration
{
    private const string InMemoryTransportSetting = "Mohist:Testing:InMemoryOrleansTransport";

    public static ISiloBuilder ConfigureMohistSilo(this ISiloBuilder silo, IConfiguration configuration)
    {
        // Production uses the well-known localhost endpoint identities. The
        // test host keeps these identities but replaces the socket transport
        // with Orleans.TestingHost's in-memory transport below.
        var siloPort = configuration.GetValue<int?>("Mohist:Silo:SiloPort") ?? EndpointOptions.DEFAULT_SILO_PORT;
        var gatewayPort = configuration.GetValue<int?>("Mohist:Silo:GatewayPort") ?? EndpointOptions.DEFAULT_GATEWAY_PORT;
        silo.UseLocalhostClustering(siloPort, gatewayPort);
        if (configuration.GetValue<bool>(InMemoryTransportSetting))
            UseTestingHostInMemoryTransport(silo);
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

        // The dispatcher grain registers a ~1s reminder, well
        // below the runtime's default MinimumReminderPeriod (~1 minute).
        // Lower the floor to 100ms so a fast cadence is accepted at
        // registration time; the grain's EventDispatcherOptions still
        // decides the actual cadence.
        silo.Configure<ReminderOptions>(options =>
        {
            options.MinimumReminderPeriod = TimeSpan.FromMilliseconds(100);
        });

        // The cluster-singleton EventDispatcherGrain
        // resolves EventDispatcherOptions from its constructor. Options
        // binding happens in the silo DI scope so the reminder cadence
        // configured under "EventDispatcher" reaches the grain regardless
        // of whether the host DI is populated.
        silo.Services.Configure<EventDispatcherOptions>(
            configuration.GetSection(EventDispatcherOptions.SectionName));

        return silo;
    }

    private static void UseTestingHostInMemoryTransport(ISiloBuilder silo)
    {
        // Microsoft.Orleans.TestingHost is a test-only dependency and must not
        // be referenced by the production project. The test host opts in via
        // configuration; resolve its transport adapter only in that process.
        var testingHost = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(
                assembly.GetName().Name,
                "Orleans.TestingHost",
                StringComparison.Ordinal))
            ?? LoadTestingHostAssembly();
        var hubType = testingHost.GetType(
            "Orleans.TestingHost.InMemoryTransport.InMemoryTransportConnectionHub",
            throwOnError: true)!;
        var hub = Activator.CreateInstance(hubType)
            ?? throw new InvalidOperationException("Could not create the Orleans in-memory transport hub.");
        var extensionsType = testingHost.GetType(
            "Orleans.TestingHost.InMemoryTransport.InMemoryTransportExtensions",
            throwOnError: true)!;
        var useTransport = extensionsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "UseInMemoryConnectionTransport"
                && method.GetParameters().Length == 2
                && method.GetParameters()[0].ParameterType == typeof(ISiloBuilder));
        useTransport.Invoke(null, [silo, hub]);
    }

    private static Assembly LoadTestingHostAssembly()
    {
        try
        {
            return Assembly.Load("Orleans.TestingHost");
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidOperationException(
                $"{InMemoryTransportSetting} requires Microsoft.Orleans.TestingHost.",
                exception);
        }
    }
}
