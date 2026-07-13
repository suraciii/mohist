using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
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
    public ControllableRunnerGrainAssignmentObserver RunnerAssignmentObserver { get; } = new();
    public ControllableRunnerGrainCloseoutObserver CloseoutObserver { get; } = new();

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
            siloBuilder.Services.AddSingleton<IRunnerGrainAssignmentObserver>(RunnerAssignmentObserver);
            siloBuilder.Services.AddSingleton<IRunnerGrainCloseoutObserver>(CloseoutObserver);
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
    private TaskCompletionSource _assignmentPrepared = NewSignal();
    private TaskCompletionSource _runnerAccepted = NewSignal();
    private TaskCompletionSource? _assignmentPreparedBlock;

    public bool FailAssignmentPrepared { get; set; }
    public bool FailRunnerAccepted { get; set; }

    public Task AssignmentPreparedAsync(string agentJobId, string runnerId, string workId)
    {
        _assignmentPrepared.TrySetResult();
        if (_assignmentPreparedBlock is not null)
            return _assignmentPreparedBlock.Task;
        return FailAssignmentPrepared
            ? Task.FromException(new InvalidOperationException("simulated activation loss after assignment preparation"))
            : Task.CompletedTask;
    }

    public Task RunnerAcceptedAsync(string agentJobId, string runnerId, string workId)
    {
        _runnerAccepted.TrySetResult();
        return FailRunnerAccepted
            ? Task.FromException(new InvalidOperationException("simulated activation loss after runner acceptance"))
            : Task.CompletedTask;
    }

    public Task WaitForRunnerAcceptedAsync() => _runnerAccepted.Task;

    public Task WaitForAssignmentPreparedAsync() => _assignmentPrepared.Task;

    public void BlockAssignmentPrepared() => _assignmentPreparedBlock ??= NewSignal();

    public void ReleaseAssignmentPrepared() => _assignmentPreparedBlock?.TrySetResult();

    public void Reset()
    {
        FailAssignmentPrepared = false;
        FailRunnerAccepted = false;
        _assignmentPreparedBlock?.TrySetResult();
        _assignmentPreparedBlock = null;
        _assignmentPrepared = NewSignal();
        _runnerAccepted = NewSignal();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ControllableRunnerGrainAssignmentObserver : IRunnerGrainAssignmentObserver
{
    private TaskCompletionSource _assignmentAdmission = NewSignal();
    private TaskCompletionSource? _assignmentAdmissionBlock;

    public Task AssignmentAdmissionAsync(string runnerId, WorkDispatch work)
    {
        _assignmentAdmission.TrySetResult();
        return _assignmentAdmissionBlock?.Task ?? Task.CompletedTask;
    }

    public Task WaitForAssignmentAdmissionAsync() => _assignmentAdmission.Task;

    public void BlockAssignmentAdmission() => _assignmentAdmissionBlock ??= NewSignal();

    public void ReleaseAssignmentAdmission() => _assignmentAdmissionBlock?.TrySetResult();

    public void Reset()
    {
        _assignmentAdmissionBlock?.TrySetResult();
        _assignmentAdmissionBlock = null;
        _assignmentAdmission = NewSignal();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ControllableRunnerGrainCloseoutObserver : IRunnerGrainCloseoutObserver
{
    private TaskCompletionSource _agentJobCloseoutStarted = NewSignal();

    public Task AgentJobCloseoutStartingAsync(string runnerId, string agentJobId, string workId)
    {
        _agentJobCloseoutStarted.TrySetResult();
        return Task.CompletedTask;
    }

    public Task WaitForAgentJobCloseoutStartingAsync() => _agentJobCloseoutStarted.Task;

    public void Reset() => _agentJobCloseoutStarted = NewSignal();

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
