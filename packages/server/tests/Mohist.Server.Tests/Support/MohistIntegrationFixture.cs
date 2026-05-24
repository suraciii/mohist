using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events;
using Mohist.Server.Hosting;
using Mohist.Server.Runner.Grains;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.Tests.Support;

public class MohistIntegrationFixture : IAsyncLifetime
{
    private readonly InMemoryEventBus _eventBus = new(
        Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryEventBus>.Instance);

    private SqliteConnection _keeper = null!;
    private MohistWebApplicationFactory _factory = null!;

    public InProcessTestCluster Cluster { get; private set; } = null!;
    public IGrainFactory Grains => Cluster.Client;
    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _factory.Services;
    public IEventBus EventBus => _eventBus;
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var dbName = $"mohist-{Guid.NewGuid():N}";
        ConnectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(ConnectionString);
        await _keeper.OpenAsync();

        var builder = new InProcessTestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.ConfigureMohistSilo();
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Mohist:SqliteConnectionString"] = ConnectionString,
                })
                .Build();
            siloBuilder.Services.AddSingleton<IConfiguration>(config);
            siloBuilder.Services.AddMohistServerCore(config);
            siloBuilder.Services.AddSingleton<IEventBus>(_ => _eventBus);
        });

        Cluster = builder.Build();
        await Cluster.DeployAsync();

        _factory = new MohistWebApplicationFactory(ConnectionString, Cluster.Client, _eventBus);
        Client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        _factory?.Dispose();
        if (Cluster is not null)
            await Cluster.DisposeAsync();
        if (_keeper is not null)
            await _keeper.DisposeAsync();
    }
}

public class MohistWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly IClusterClient _clusterClient;
    private readonly IEventBus _eventBus;

    public MohistWebApplicationFactory(string connectionString, IClusterClient clusterClient, IEventBus eventBus)
    {
        _connectionString = connectionString;
        _clusterClient = clusterClient;
        _eventBus = eventBus;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Mohist:UseExternalOrleans", "true");
        builder.UseSetting("Mohist:SqliteConnectionString", _connectionString);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:UseExternalOrleans"] = "true",
                ["Mohist:SqliteConnectionString"] = _connectionString,
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IClusterClient>(_clusterClient);
            services.AddSingleton<IGrainFactory>(_clusterClient);
            services.AddSingleton<IEventBus>(_eventBus);
        });
    }
}

[CollectionDefinition("MohistIntegration", DisableParallelization = true)]
public class MohistIntegrationCollection : ICollectionFixture<MohistIntegrationFixture>;
