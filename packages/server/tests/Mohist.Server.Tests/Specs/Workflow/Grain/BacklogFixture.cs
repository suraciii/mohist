using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Tests.Support;
using Orleans;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

public class BacklogFixture : IAsyncLifetime
{
    public InProcessTestCluster Cluster { get; private set; } = null!;
    public IGrainFactory Grains => Cluster.Client;
    public string ConnectionString => _keeper.ConnectionString;

    private SqliteConnection _keeper = null!;

    public Task InitializeAsync()
    {
        var dbName = $"mohist-backlog-test-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();

        using (var db = GrainTestConfig.CreateDbContext(connectionString))
            db.Database.Migrate();

        var builder = new InProcessTestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        ConfigureCluster(builder, connectionString);
        Cluster = builder.Build();
        return Cluster.DeployAsync();
    }

    public Task DisposeAsync()
    {
        Cluster?.Dispose();
        _keeper?.Dispose();
        return Task.CompletedTask;
    }

    public static void ConfigureCluster(InProcessTestClusterBuilder builder, string connectionString)
    {
        builder.ConfigureSilo((_, siloBuilder) =>
            GrainTestConfig.ConfigureSilo(siloBuilder, connectionString,
                new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance),
                new NoopEventStore()));
    }
}
