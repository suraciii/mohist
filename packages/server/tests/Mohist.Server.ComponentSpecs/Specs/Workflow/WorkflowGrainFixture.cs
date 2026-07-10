using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.ComponentSpecs.Support;
using Orleans;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.ComponentSpecs.Specs.Workflow;

public class WorkflowGrainFixture : IAsyncLifetime
{
    public InProcessTestCluster Cluster { get; private set; } = null!;
    public IGrainFactory Grains => Cluster.Client;
    public IEventPublisher EventBus => _sharedEventBus;
    public RecordingEventStore EventStore => _sharedEventStore;
    public string ConnectionString => _keeper.ConnectionString;
    public FakeRunnerWorkspaceClient RunnerWorkspace => Cluster.GetSiloServiceProvider(null).GetRequiredService<FakeRunnerWorkspaceClient>();
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public IReminderTable ReminderTable => Cluster.GetSiloServiceProvider(null).GetRequiredService<IReminderTable>();
    public ControllableReminderTable ControllableReminderTable => Cluster.GetSiloServiceProvider(null).GetRequiredService<ControllableReminderTable>();

    private readonly InMemoryEventBus _sharedEventBus = new(
        new RecordingEventStore(),
        NullLogger<InMemoryEventBus>.Instance);
    private readonly RecordingEventStore _sharedEventStore = new();
    private SqliteConnection _keeper = null!;

    public async Task InitializeAsync()
    {
        var dbName = $"mohist-test-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();

        MigratedSqliteTemplate.CopyTo(_keeper);

        var builder = new InProcessTestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.ConfigureSilo((_, siloBuilder) =>
            GrainTestConfig.ConfigureSilo(siloBuilder, connectionString, _sharedEventBus, _sharedEventStore, TimeProvider));
        Cluster = builder.Build();
        await Cluster.DeployAsync();

        // Wire the bus-side subscriptions that the production pipeline
        // registers via AddCloudEventHandlersFromAssembly. Test fixtures
        // do not run that registration path, so we add them explicitly
        // here after the cluster has been deployed (the handler needs a
        // live cluster client to dispatch lock releases into the running
        // silo). New bus-side handlers must be added here so tests
        // exercise the real dispatch path.
        var handler = new WorkflowStageLockReleaseHandler(
            Cluster.Client,
            NullLogger<WorkflowStageLockReleaseHandler>.Instance);
        _sharedEventBus.AddSubscription(new Subscription(
            "com.mohist.workflow.stage.completed|com.mohist.workflow.stage.failed",
            handler,
            (h, e, ct) => ((WorkflowStageLockReleaseHandler)h).HandleAsync(e, ct)));
    }

    public Task DisposeAsync()
    {
        Cluster?.Dispose();
        _keeper?.DisposeAsync();
        return Task.CompletedTask;
    }

}
