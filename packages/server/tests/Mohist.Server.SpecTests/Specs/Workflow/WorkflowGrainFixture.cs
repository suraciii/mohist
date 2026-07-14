using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow;

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
    public ControllableDispatchPollObserver DispatchPollObserver { get; } = new();

    private readonly RecordingEventStore _sharedEventStore = new();
    private readonly InMemoryEventBus _sharedEventBus;
    private SqliteConnection _keeper = null!;

    public WorkflowGrainFixture()
    {
        _sharedEventBus = new InMemoryEventBus(
            _sharedEventStore,
            TimeProvider,
            NullLogger<InMemoryEventBus>.Instance);
    }

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
        {
            GrainTestConfig.ConfigureSilo(siloBuilder, connectionString, _sharedEventBus, _sharedEventStore, TimeProvider);
            siloBuilder.Services.AddSingleton<IDispatchPollObserver>(DispatchPollObserver);
        });
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public Task DisposeAsync()
    {
        Cluster?.Dispose();
        _keeper?.DisposeAsync();
        return Task.CompletedTask;
    }

}

public sealed class ControllableDispatchPollObserver : IDispatchPollObserver
{
    private TaskCompletionSource _runnerInfoObserved = NewSignal();
    private TaskCompletionSource? _afterRunnerInfoBlock;

    public Task AfterRunnerInfoAsync(string runnerId)
    {
        _runnerInfoObserved.TrySetResult();
        return _afterRunnerInfoBlock?.Task ?? Task.CompletedTask;
    }

    public Task WaitForRunnerInfoAsync() => _runnerInfoObserved.Task;

    public void BlockAfterRunnerInfo() => _afterRunnerInfoBlock ??= NewSignal();

    public void ReleaseAfterRunnerInfo() => _afterRunnerInfoBlock?.TrySetResult();

    public void Reset()
    {
        _afterRunnerInfoBlock?.TrySetResult();
        _afterRunnerInfoBlock = null;
        _runnerInfoObserved = NewSignal();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
