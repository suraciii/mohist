using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security;
using Mohist.Server.Notifications;
using Orleans.Hosting;
using Xunit;

namespace Mohist.Server.UnitTests.Foundation;

public sealed class MohistServiceGraphRegistrationTests
{
    [Fact]
    public void HostAndSiloConfiguration_RegisterOneApplicationServiceGraph()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = "Data Source=:memory:",
            })
            .Build();
        var services = new ServiceCollection();
        var silo = new TestSiloBuilder(services, configuration);

        services.ConfigureMohistServices(configuration);
        silo.ConfigureMohistSilo(configuration);

        AssertSingleRegistration<InMemoryEventBus>(services);
        AssertSingleRegistration<IEventPublisher>(services);
        AssertSingleRegistration<IEventStore>(services);
        AssertSingleRegistration<IDeadLetterStore>(services);
        AssertSingleRegistration<EventDispatcherService>(services);
        AssertSingleRegistration<IEnumerable<Subscription>>(services);
        AssertSingleRegistration<InboxProjectionHandler>(services);
        AssertSingleRegistration<RoutingDispatchHandler>(services);
        AssertSingleRegistration<HermesIssueNotificationRenderer>(services);
        AssertSingleRegistration<IHermesIssueNotificationDispatcher>(services);
        AssertSingleRegistration<IAgentJobDispatchObserver>(services);
        AssertSingleRegistration<OperatorCredential>(services);
        AssertSingleRegistration<TimeProvider>(services);
    }

    private static void AssertSingleRegistration<T>(IServiceCollection services) =>
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(T));

    private sealed class TestSiloBuilder(
        IServiceCollection services,
        IConfiguration configuration) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = services;
        public IConfiguration Configuration { get; } = configuration;
    }
}
