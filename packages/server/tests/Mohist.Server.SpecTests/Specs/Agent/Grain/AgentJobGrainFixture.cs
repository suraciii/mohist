using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

public sealed class AgentJobGrainFixture : IAsyncLifetime
{
    public InProcessTestCluster Cluster { get; private set; } = null!;
    public IGrainFactory Grains => Cluster.Client;
    public IEventPublisher EventBus => _sharedEventBus;
    public RecordingEventStore EventStore => _sharedEventStore;
    public string ConnectionString => _keeper.ConnectionString;
    public FakeRunnerWorkspaceClient RunnerWorkspace => Cluster.GetSiloServiceProvider(null).GetRequiredService<FakeRunnerWorkspaceClient>();
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public ControllableAgentJobDispatchObserver DispatchObserver { get; } = new();

    private readonly InMemoryEventBus _sharedEventBus = new(new RecordingEventStore(), System.TimeProvider.System, NullLogger<InMemoryEventBus>.Instance);
    private readonly RecordingEventStore _sharedEventStore = new();
    private SqliteConnection _keeper = null!;

    public Task InitializeAsync()
    {
        var dbName = $"mohist-agent-job-test-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();

        MigratedSqliteTemplate.CopyTo(_keeper);

        var builder = new InProcessTestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            GrainTestConfig.ConfigureSilo(siloBuilder, connectionString, _sharedEventBus, _sharedEventStore, TimeProvider);
            siloBuilder.Services.AddSingleton<IAgentJobDispatchObserver>(DispatchObserver);
        });
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

public sealed class ControllableAgentJobDispatchObserver : IAgentJobDispatchObserver
{
    private TaskCompletionSource _runnerAccepted = NewSignal();

    public bool FailRunnerAccepted { get; set; }

    public Task AssignmentPreparedAsync(string agentJobId, string runnerId, string workId) => Task.CompletedTask;

    public Task RunnerAcceptedAsync(string agentJobId, string runnerId, string workId)
    {
        _runnerAccepted.TrySetResult();
        return FailRunnerAccepted
            ? Task.FromException(new InvalidOperationException("simulated activation loss after runner acceptance"))
            : Task.CompletedTask;
    }

    public Task WaitForRunnerAcceptedAsync() => _runnerAccepted.Task;

    public void Reset()
    {
        FailRunnerAccepted = false;
        _runnerAccepted = NewSignal();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
