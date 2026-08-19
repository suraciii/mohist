using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Api;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;
using AgentDomain = Mohist.Server.Agent.Domain.Agent;

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
    public AgentLaunchParticipantProbe LaunchFaults { get; } = new();
    public ControllableAgentSessionTranscriptPersistence SessionPersistence { get; } = new();
    public RecordingSessionStopDelivery StopDelivery { get; } = new();
    public RecordingSessionWorkPort WorkPort { get; } = new();
    public AgentSessionPersistenceTestProbe Persistence { get; }
    public AgentSessionStatePersistenceFailureProbe SessionStatePersistence { get; } = new();
    public RunnerUpdateOperationWriteFailureProbe OperationWriteFailures =>
        Cluster.GetSiloServiceProvider(null).GetRequiredService<RunnerUpdateOperationWriteFailureProbe>();

    private readonly InMemoryEventBus _sharedEventBus;
    private readonly RecordingEventStore _sharedEventStore = new();
    private TestSqliteDatabase _database = null!;

    public AgentJobGrainFixture()
    {
        Persistence = new AgentSessionPersistenceTestProbe(
            () => TimeProvider.Advance(TimeSpan.FromSeconds(1)));
        _sharedEventBus = new InMemoryEventBus(
            _sharedEventStore,
            TimeProvider,
            NullLogger<InMemoryEventBus>.Instance);
    }

    public ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        var connectionString = _database.ConnectionString;

        var builder = new InProcessTestClusterBuilder().UseLogicalPorts();
        builder.Options.InitialSilosCount = 1;
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            GrainTestConfig.ConfigureSilo(
                siloBuilder,
                connectionString,
                _sharedEventBus,
                _sharedEventStore,
                TimeProvider,
                Persistence);
            siloBuilder.Services.AddSingleton(SessionStatePersistence);
            siloBuilder.Services.RemoveAll<IAgentSessionStore>();
            siloBuilder.Services.AddScoped<AgentSessionStore>();
            siloBuilder.Services.AddScoped<IAgentSessionStore>(services =>
                new FailingAgentSessionStore(
                    services.GetRequiredService<AgentSessionStore>(),
                    services.GetRequiredService<AgentSessionStatePersistenceFailureProbe>()));
            siloBuilder.Services.RemoveAll<ISessionStopDelivery>();
            siloBuilder.Services.AddSingleton<ISessionStopDelivery>(StopDelivery);
            siloBuilder.Services.RemoveAll<ISessionWorkPort>();
            siloBuilder.Services.AddSingleton<ISessionWorkPort>(WorkPort);
            siloBuilder.Services.RemoveAll<IAgentLaunchParticipantProbe>();
            siloBuilder.Services.AddSingleton<IAgentLaunchParticipantProbe>(LaunchFaults);
            siloBuilder.Services.AddSingleton<IAgentJobDispatchObserver>(DispatchObserver);
            siloBuilder.Services.AddSingleton(SessionPersistence);
            siloBuilder.Services.RemoveAll<IAgentSessionTranscriptStore>();
            siloBuilder.Services.AddScoped<IAgentSessionTranscriptStore>(provider =>
                new FailingAgentSessionTranscriptStore(
                    provider.GetRequiredService<IDbContextFactory<Mohist.Server.Infrastructure.Data.Db.MohistDbContext>>(),
                    SessionPersistence));
        });
        Cluster = builder.Build();
        return new ValueTask(Cluster.DeployAsync());
    }

    public sealed class RecordingSessionStopDelivery : ISessionStopDelivery
    {
        private readonly object _gate = new();
        private readonly List<SessionStopDeliveryRequest> _requests = [];
        private readonly Queue<RunnerStopReply?> _responses = [];

        public IReadOnlyList<SessionStopDeliveryRequest> Requests
        {
            get
            {
                lock (_gate)
                    return _requests.ToArray();
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _requests.Clear();
                _responses.Clear();
            }
        }

        public void Enqueue(RunnerStopReply? response)
        {
            lock (_gate)
                _responses.Enqueue(response);
        }

        public Task<SessionStopDeliveryResponse> DispatchAsync(
            SessionStopDeliveryRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _requests.Add(request);
                return Task.FromResult(new SessionStopDeliveryResponse(
                    _responses.Count > 0 ? _responses.Dequeue() : null,
                    DispatchStarted: true));
            }
        }
    }

    public sealed class RecordingSessionWorkPort : ISessionWorkPort
    {
        private readonly object _gate = new();
        private readonly List<SessionWorkObservationRequest> _requests = [];

        public IReadOnlyList<SessionWorkObservationRequest> Requests
        {
            get
            {
                lock (_gate)
                    return _requests.ToArray();
            }
        }

        public void Reset()
        {
            lock (_gate)
                _requests.Clear();
        }

        public Task<bool> BindAgentExecutionAsync(
            SessionWorkflowExecutionBinding binding,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> CanStartAgentCleanupAsync(
            SessionWorkflowExecutionBinding binding,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task ObserveAgentExecutionAsync(
            SessionWorkflowExecutionBinding binding,
            SessionWorkflowObservationKind kind,
            string reasonCode,
            string? message = null,
            string? stopOperationId = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _requests.Add(new SessionWorkObservationRequest(binding, kind, reasonCode, message, stopOperationId));
            return Task.CompletedTask;
        }
    }

    public sealed record SessionWorkObservationRequest(
        SessionWorkflowExecutionBinding Binding,
        SessionWorkflowObservationKind Kind,
        string ReasonCode,
        string? Message,
        string? StopOperationId);

    public ValueTask DisposeAsync()
    {
        Cluster?.Dispose();
        _database?.Dispose();
        return ValueTask.CompletedTask;
    }

    public async Task ClearActiveAgentJobsAsync()
    {
        var management = Grains.GetGrain<IManagementGrain>(0);
        var activations = await management.GetDetailedGrainStatistics();
        var jobKeys = activations
            .Where(stat => stat.GrainType.Contains(nameof(AgentJobGrain), StringComparison.Ordinal))
            .Select(stat => stat.GrainId.Key.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var jobKey in jobKeys)
        {
            var job = Grains.GetGrain<IAgentJobGrain>(jobKey);
            var snapshot = await job.GetRuntimeSnapshotAsync();
            if (snapshot.Status is AgentJobStatus.Completed or AgentJobStatus.Failed)
                continue;

            if (snapshot.RunnerId is not null && snapshot.CurrentWorkId is not null)
            {
                await job.ReportResultAsync(
                    snapshot.RunnerId,
                    snapshot.CurrentWorkId,
                    new WorkResult("completed"));
            }

            if (await job.GetStatusAsync() is not (AgentJobStatus.Completed or AgentJobStatus.Failed))
                await job.FailAsync("test-cleanup", "test-cleanup-agent");
        }
    }

    public async Task SeedAgentAsync(string projectId, string agentId, int? maxConcurrentRuns)
    {
        var agent = new AgentDomain
        {
            Id = agentId,
            ProjectId = projectId,
            Name = agentId,
            Description = "spec",
            Instructions = "spec",
            Skills = [],
            MaxConcurrentRuns = maxConcurrentRuns,
            Status = AgentStatus.Active,
            CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
            UpdatedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        var id = GrainKey.Agent(projectId, agentId);
        var factory = Cluster.GetSiloServiceProvider(null).GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.Agents.FindAsync(id);
        if (row is null)
        {
            db.Agents.Add(new AgentRow
            {
                Id = id,
                ProjectId = projectId,
                Name = agent.Name,
                Status = agent.Status,
                State = AgentStore.Serialize(agent),
            });
        }
        else
        {
            row.ProjectId = projectId;
            row.Name = agent.Name;
            row.Status = agent.Status;
            row.State = AgentStore.Serialize(agent);
        }
        await db.SaveChangesAsync();
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

    public Task AssignmentPrepared => _assignmentPrepared.Task;

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

    public Task WaitForRunnerAcceptedAsync() => WaitForSignalAsync(
        _runnerAccepted,
        "AgentJob dispatch observer runner accepted");

    public Task WaitForAssignmentPreparedAsync() => WaitForSignalAsync(
        _assignmentPrepared,
        "AgentJob dispatch observer assignment prepared");

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

    private static Task WaitForSignalAsync(TaskCompletionSource signal, string description) =>
        TestWait.ForAsync(
            async () =>
            {
                if (!signal.Task.IsCompleted)
                    await Task.Run(static () => { });
                return signal.Task.IsCompleted;
            },
            completed => completed,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            description);
}
