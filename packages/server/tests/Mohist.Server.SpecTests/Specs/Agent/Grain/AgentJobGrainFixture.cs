using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Sessions;
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
    public string ConnectionString => _database.ConnectionString;
    public FakeRunnerWorkspaceClient RunnerWorkspace => Cluster.GetSiloServiceProvider(null).GetRequiredService<FakeRunnerWorkspaceClient>();
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public ControllableAgentJobDispatchObserver DispatchObserver { get; } = new();
    public ControllableRunnerGrainAssignmentObserver RunnerAssignmentObserver { get; } = new();
    public ControllableRunnerGrainCloseoutObserver CloseoutObserver { get; } = new();
    public ControllableAgentSessionTranscriptPersistence SessionPersistence { get; } = new();

    private readonly InMemoryEventBus _sharedEventBus;
    private readonly RecordingEventStore _sharedEventStore = new();
    private TestSqliteDatabase _database = null!;

    public AgentJobGrainFixture()
    {
        _sharedEventBus = new InMemoryEventBus(
            _sharedEventStore,
            TimeProvider,
            NullLogger<InMemoryEventBus>.Instance);
    }

    public Task InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        var connectionString = _database.ConnectionString;

        var builder = new InProcessTestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            GrainTestConfig.ConfigureSilo(siloBuilder, connectionString, _sharedEventBus, _sharedEventStore, TimeProvider);
            siloBuilder.Services.AddSingleton<IAgentJobDispatchObserver>(DispatchObserver);
            siloBuilder.Services.AddSingleton<IRunnerGrainAssignmentObserver>(RunnerAssignmentObserver);
            siloBuilder.Services.AddSingleton<IRunnerGrainCloseoutObserver>(CloseoutObserver);
            siloBuilder.Services.AddSingleton(SessionPersistence);
            siloBuilder.Services.RemoveAll<IAgentSessionTranscriptStore>();
            siloBuilder.Services.AddScoped<IAgentSessionTranscriptStore>(provider =>
                new FailingAgentSessionTranscriptStore(
                    provider.GetRequiredService<IDbContextFactory<Mohist.Server.Infrastructure.Data.Db.MohistDbContext>>(),
                    SessionPersistence));
        });
        Cluster = builder.Build();
        return Cluster.DeployAsync();
    }

    public Task DisposeAsync()
    {
        Cluster?.Dispose();
        _database?.Dispose();
        return Task.CompletedTask;
    }
}

public sealed class ControllableAgentSessionTranscriptPersistence
{
    private int _failuresRemaining;

    /// <summary>
    /// Adds <paramref name="count"/> pending failures to the queue. Each
    /// AgentSession transcript save consumes one failure until the
    /// counter is back to zero. Setting <see cref="FailNext"/> = true
    /// adds a single failure; clearing it does nothing (use
    /// <see cref="ResetFailures"/> when you want to allow saves again).
    /// </summary>
    public bool FailNext
    {
        get => _failuresRemaining > 0;
        set
        {
            if (value) Interlocked.Increment(ref _failuresRemaining);
        }
    }

    public void QueueFailures(int count)
    {
        if (count > 0) Interlocked.Add(ref _failuresRemaining, count);
    }

    public void ResetFailures()
    {
        Interlocked.Exchange(ref _failuresRemaining, 0);
    }

    public void ConsumeFailure()
    {
        if (_failuresRemaining > 0)
            Interlocked.Decrement(ref _failuresRemaining);
    }
}

internal sealed class FailingAgentSessionTranscriptStore : IAgentSessionTranscriptStore
{
    private readonly IAgentSessionTranscriptStore _inner;
    private readonly ControllableAgentSessionTranscriptPersistence _control;

    public FailingAgentSessionTranscriptStore(
        IDbContextFactory<Mohist.Server.Infrastructure.Data.Db.MohistDbContext> dbFactory,
        ControllableAgentSessionTranscriptPersistence control)
    {
        _inner = new AgentSessionTranscriptStore(dbFactory);
        _control = control;
    }

    public async Task SaveAsync(AgentSessionTranscriptFlush transcript, CancellationToken ct = default)
    {
        if (_control.FailNext)
        {
            _control.ConsumeFailure();
            throw new InvalidOperationException(
                "simulated AgentSession transcript-store failure for terminal-delivery retry test");
        }
        await _inner.SaveAsync(transcript, ct);
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
