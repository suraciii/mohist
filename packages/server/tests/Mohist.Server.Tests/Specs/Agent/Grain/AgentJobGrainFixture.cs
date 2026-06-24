using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Tests.Support;
using Orleans;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.Tests.Specs.Agent.Grain;

public sealed class AgentJobGrainFixture : IAsyncLifetime
{
    public InProcessTestCluster Cluster { get; private set; } = null!;
    public IGrainFactory Grains => Cluster.Client;
    public IEventPublisher EventBus => _sharedEventBus;
    public RecordingEventStore EventStore => _sharedEventStore;
    public string ConnectionString => _keeper.ConnectionString;
    public FakeRunnerWorkspaceClient RunnerWorkspace => Cluster.GetSiloServiceProvider(null).GetRequiredService<FakeRunnerWorkspaceClient>();
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private readonly InMemoryEventBus _sharedEventBus = new(NullLogger<InMemoryEventBus>.Instance);
    private readonly RecordingEventStore _sharedEventStore = new();
    private SqliteConnection _keeper = null!;

    public Task InitializeAsync()
    {
        var dbName = $"mohist-agent-job-test-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();

        using (var db = GrainTestConfig.CreateDbContext(connectionString))
            db.Database.Migrate();

        var builder = new InProcessTestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.ConfigureSilo((_, siloBuilder) =>
            GrainTestConfig.ConfigureSilo(siloBuilder, connectionString, _sharedEventBus, _sharedEventStore, TimeProvider));
        Cluster = builder.Build();
        return Cluster.DeployAsync();
    }

    public Task DisposeAsync()
    {
        Cluster?.Dispose();
        _keeper?.DisposeAsync();
        return Task.CompletedTask;
    }
}
