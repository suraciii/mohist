using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Events.Grains;
using Mohist.Server.Events.Hosting;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Specs.Issue.Profile;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Services.Prompts;
using Orleans;
using Orleans.Configuration;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

/// <summary>
/// Captures every CloudEvent <see cref="IEventStore.AppendAsync"/> call and
/// serves the same events back from <see cref="ListUndeliveredAsync"/> as
/// fresh undelivered rows. Lets spec tests drive the dispatcher's
/// pull–fan-out–mark cycle without a real EF store — the dispatcher only
/// needs a controllable <see cref="IEventStore"/> seam.
/// </summary>
public sealed class CapturingEventStore : IEventStore
{
    private readonly List<UndeliveredEvent> _rows = [];
    private long _nextId;
    private readonly object _gate = new();

    public Func<CloudEvent, bool>? ThrowOnAppend { get; set; }

    public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default)
    {
        if (ThrowOnAppend?.Invoke(envelope) == true)
            throw new InvalidOperationException("simulated event append failure");
        lock (_gate)
        {
            _rows.Add(new UndeliveredEvent(
                Origin: ResolveOrigin(envelope.Source.ToString()),
                Id: ++_nextId,
                Source: envelope.Source.ToString(),
                EventId: envelope.Id,
                Type: envelope.Type,
                Time: envelope.Time,
                SpecVersion: envelope.SpecVersion,
                Subject: envelope.Subject,
                DataContentType: envelope.DataContentType ?? "application/json",
                Data: envelope.Data ?? System.Text.Json.JsonDocument.Parse("null").RootElement,
                ExtensionsJson: envelope.Extensions.Count == 0
                    ? "{}"
                    : System.Text.Json.JsonSerializer.Serialize(envelope.Extensions, CloudEvent.JsonOptions)));
        }
        return Task.CompletedTask;
    }

    public Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default) =>
        AppendAsync(envelope, ct);

    public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string projectId, int epicNumber, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task<IReadOnlyList<StoredCloudEvent>> ListAgentJobEventsAsync(string agentJobId, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task MarkDispatchedAsync(
        EventOrigin origin,
        string source,
        long id,
        DateTimeOffset dispatchedAt,
        CancellationToken ct = default)
    {
        lock (_gate) { _rows.RemoveAll(r => r.Origin == origin && r.Source == source && r.Id == id); }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<UndeliveredEvent>>(_rows
                .OrderBy(r => r.Source, StringComparer.Ordinal)
                .ThenBy(r => r.Id)
                .Take(limit)
                .Select(r => r)
                .ToList());
        }
    }

    public int PendingCount
    {
        get { lock (_gate) { return _rows.Count; } }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _rows.Clear();
            _nextId = 0;
            ThrowOnAppend = null;
        }
    }

    /// <summary>
    /// Re-queues a row that was previously marked dispatched so the next
    /// <see cref="ListUndeliveredAsync"/> returns it. Mirrors the production
    /// path where <c>DeadLetterStore.RetryAsync</c> re-nulls the source
    /// event's <c>DispatchedAt</c>.
    /// </summary>
    public void ReQueueForRedelivery(string origin, string source, long id)
    {
        var originEnum = ParseOriginName(origin);
        lock (_gate)
        {
            var existing = _rows.FirstOrDefault(e =>
                e.Origin == originEnum && e.Source == source && e.Id == id);
            if (existing is null)
            {
                _rows.Add(new UndeliveredEvent(
                    Origin: originEnum,
                    Id: id,
                    Source: source,
                    EventId: $"evt-retry-{id}",
                    Type: "com.mohist.retry",
                    Time: DateTimeOffset.UnixEpoch,
                    SpecVersion: "1.0",
                    Subject: null,
                    DataContentType: "application/json",
                    Data: System.Text.Json.JsonDocument.Parse("null").RootElement,
                    ExtensionsJson: "{}"));
            }
        }
    }

    internal StateSnapshot CaptureState()
    {
        lock (_gate)
        {
            return new StateSnapshot(_rows.ToList(), _nextId);
        }
    }

    internal void RestoreState(StateSnapshot snapshot)
    {
        lock (_gate)
        {
            _rows.Clear();
            _rows.AddRange(snapshot.Rows);
            _nextId = snapshot.NextId;
        }
    }

    internal sealed record StateSnapshot(IReadOnlyList<UndeliveredEvent> Rows, long NextId);

    private static EventOrigin ResolveOrigin(string source)
    {
        if (source.StartsWith("/mohist/workflow-runs/", StringComparison.Ordinal)) return EventOrigin.WorkflowRun;
        if (source.StartsWith("/mohist/issues/", StringComparison.Ordinal)) return EventOrigin.Issue;
        if (source.StartsWith("/mohist/epics/", StringComparison.Ordinal)) return EventOrigin.Epic;
        if (source.StartsWith("/mohist/agent-session/", StringComparison.Ordinal)) return EventOrigin.AgentSession;
        if (source.StartsWith("/mohist/inbox", StringComparison.Ordinal)) return EventOrigin.WorkflowRun;
        return EventOrigin.WorkflowRun;
    }

    private static EventOrigin ParseOriginName(string origin) => origin switch
    {
        nameof(EventOrigin.WorkflowRun) => EventOrigin.WorkflowRun,
        nameof(EventOrigin.Issue) => EventOrigin.Issue,
        nameof(EventOrigin.Epic) => EventOrigin.Epic,
        nameof(EventOrigin.AgentSession) => EventOrigin.AgentSession,
        _ => throw new InvalidOperationException($"Unknown event origin '{origin}'."),
    };
}

/// <summary>
/// In-memory <see cref="IDeadLetterStore"/> for the dispatcher fixture.
/// Records every dead-letter write and supports the query/get paths so
/// spec tests can assert the grain → service → dead-letter wiring.
/// </summary>
public sealed class CapturingDeadLetterStore : IDeadLetterStore
{
    private readonly object _gate = new();
    private readonly List<DeadLetterRow> _rows = [];
    private readonly CapturingEventStore _events;
    private long _nextId;

    public bool ThrowAfterSourceMark { get; set; }

    public CapturingDeadLetterStore(CapturingEventStore events)
    {
        _events = events;
    }

    public Task WriteAsync(DeadLetterRow row, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var assignedId = row.DeadLetterId == 0 ? ++_nextId : row.DeadLetterId;
            _rows.Add(new DeadLetterRow
            {
                DeadLetterId = assignedId,
                Origin = row.Origin,
                Id = row.Id,
                Source = row.Source,
                EventId = row.EventId,
                Type = row.Type,
                Time = row.Time,
                SpecVersion = row.SpecVersion,
                Subject = row.Subject,
                DataContentType = row.DataContentType,
                Data = row.Data,
                ExtensionsJson = row.ExtensionsJson,
                FailingHandler = row.FailingHandler,
                ErrorMessage = row.ErrorMessage,
                ErrorStack = row.ErrorStack,
                AttemptCount = row.AttemptCount,
                DeadLetteredAt = row.DeadLetteredAt,
            });
        }
        return Task.CompletedTask;
    }

    public async Task SettleAsync(
        UndeliveredEvent sourceEvent,
        IReadOnlyList<DeadLetterRow> rows,
        DateTimeOffset dispatchedAt,
        CancellationToken ct = default)
    {
        var eventSnapshot = _events.CaptureState();
        List<DeadLetterRow> rowSnapshot;
        long nextIdSnapshot;
        lock (_gate)
        {
            rowSnapshot = _rows.Select(Clone).ToList();
            nextIdSnapshot = _nextId;
        }

        try
        {
            await _events.MarkDispatchedAsync(
                sourceEvent.Origin,
                sourceEvent.Source,
                sourceEvent.Id,
                dispatchedAt,
                ct);
            if (ThrowAfterSourceMark)
                throw new InvalidOperationException("simulated post-mark settlement failure");
            foreach (var row in rows)
                await WriteAsync(row, ct);
        }
        catch
        {
            _events.RestoreState(eventSnapshot);
            lock (_gate)
            {
                _rows.Clear();
                _rows.AddRange(rowSnapshot);
                _nextId = nextIdSnapshot;
            }
            throw;
        }
    }

    public Task<IReadOnlyList<DeadLetterRow>> QueryAsync(string? failingHandler, int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IEnumerable<DeadLetterRow> q = _rows.Where(row => row.Status != DeadLetterStatus.Resolved);
            if (!string.IsNullOrEmpty(failingHandler))
                q = q.Where(r => r.FailingHandler == failingHandler);
            return Task.FromResult<IReadOnlyList<DeadLetterRow>>(q
                .OrderBy(r => r.DeadLetteredAt)
                .ThenBy(r => r.DeadLetterId)
                .Take(limit)
                .ToList());
        }
    }

    public Task<IReadOnlyList<DeadLetterRow>> ListByHandlerAsync(
        string handler,
        int limit = 100,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DeadLetterRow>>(_rows
                .Where(r => r.FailingHandler == handler)
                .OrderByDescending(r => r.DeadLetteredAt)
                .ThenByDescending(r => r.DeadLetterId)
                .Take(limit)
                .ToList());
        }
    }

    public Task<IReadOnlyList<DeadLetterRow>> ListByTimeRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int limit = 100,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DeadLetterRow>>(_rows
                .Where(r => r.DeadLetteredAt >= from && r.DeadLetteredAt < to)
                .OrderBy(r => r.DeadLetteredAt)
                .ThenBy(r => r.DeadLetterId)
                .Take(limit)
                .ToList());
        }
    }

    public Task RetryAsync(long deadLetterId, CancellationToken ct = default)
    {
        DeadLetterRow? row;
        lock (_gate)
        {
            row = _rows.FirstOrDefault(r => r.DeadLetterId == deadLetterId);
        }
        if (row is null)
            throw new InvalidOperationException($"Dead-letter row '{deadLetterId}' was not found.");
        _events.ReQueueForRedelivery(row.Origin, row.Source, row.Id);
        return Task.CompletedTask;
    }

    public Task<DeadLetterRow?> GetAsync(long deadLetterId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.FirstOrDefault(r => r.DeadLetterId == deadLetterId));
        }
    }

    public Task<DeadLetterRow?> StartRedeliveryAsync(long deadLetterId, DateTimeOffset attemptedAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var row = _rows.FirstOrDefault(row => row.DeadLetterId == deadLetterId);
            if (row is null || row.Status == DeadLetterStatus.Resolved)
                return Task.FromResult<DeadLetterRow?>(null);
            row.Status = DeadLetterStatus.Redelivering;
            row.RedeliveryAttemptedAt = attemptedAt;
            return Task.FromResult<DeadLetterRow?>(row);
        }
    }

    public Task RecordRedeliveryFailureAsync(long deadLetterId, string errorMessage, string? errorStack, int attemptCount, DateTimeOffset attemptedAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var row = _rows.Single(row => row.DeadLetterId == deadLetterId);
            row.Status = DeadLetterStatus.Pending;
            row.ErrorMessage = errorMessage;
            row.ErrorStack = errorStack;
            row.AttemptCount = attemptCount;
            row.RedeliveryAttemptedAt = attemptedAt;
        }
        return Task.CompletedTask;
    }

    public Task ResolveAsync(long deadLetterId, DateTimeOffset resolvedAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var row = _rows.Single(row => row.DeadLetterId == deadLetterId);
            row.Status = DeadLetterStatus.Resolved;
            row.ResolvedAt = resolvedAt;
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(long deadLetterId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _rows.RemoveAll(row => row.DeadLetterId == deadLetterId);
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<DeadLetterRow> Written
    {
        get { lock (_gate) { return _rows.Where(row => row.Status != DeadLetterStatus.Resolved).ToList(); } }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _rows.Clear();
            _nextId = 0;
            ThrowAfterSourceMark = false;
        }
    }

    private static DeadLetterRow Clone(DeadLetterRow row) =>
        new()
        {
            DeadLetterId = row.DeadLetterId,
            Origin = row.Origin,
            Id = row.Id,
            Source = row.Source,
            EventId = row.EventId,
            Type = row.Type,
            Time = row.Time,
            SpecVersion = row.SpecVersion,
            Subject = row.Subject,
            DataContentType = row.DataContentType,
            Data = row.Data,
            ExtensionsJson = row.ExtensionsJson,
            FailingHandler = row.FailingHandler,
            ErrorMessage = row.ErrorMessage,
            ErrorStack = row.ErrorStack,
            AttemptCount = row.AttemptCount,
            DeadLetteredAt = row.DeadLetteredAt,
            Status = row.Status,
            RedeliveryAttemptedAt = row.RedeliveryAttemptedAt,
            ResolvedAt = row.ResolvedAt,
        };
}

public sealed class DispatcherFixture : IAsyncLifetime
{
    public InProcessTestCluster Cluster { get; private set; } = null!;
    public IGrainFactory Grains => Cluster.Client;
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
    public CapturingEventStore EventStore { get; } = new();
    public CapturingEventPublisher EventPublisher { get; } = new();
    public CapturingDeadLetterStore DeadLetterStore { get; }
    public FakeRunnerWorkspaceClient RunnerWorkspace { get; private set; } = null!;
    public SharedReminderTable ReminderTable { get; } = new();
    public RecordingBackgroundTaskLauncher BackgroundTasks { get; } = new();

    public IEventDispatcherGrain EventDispatcher => Grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global);

    /// <summary>
    /// Per-silo call lists shared by the test handlers
    /// (<see cref="DispatcherClosedGenericHandler"/>,
    /// <see cref="DispatcherCatchAllHandler"/>,
    /// <see cref="DispatcherSpecificHandler"/>) via the silo's
    /// <see cref="IServiceProvider"/>. The handlers resolve the
    /// fixture instance from DI so they can record invocations here.
    /// </summary>
    public List<string> ClosedGenericInvocations { get; } = [];
    public List<string> CatchAllInvocations { get; } = [];
    public List<string> SpecificInvocations { get; } = [];
    private readonly Dictionary<string, TaskCompletionSource> _specificDeliverySignals = new(StringComparer.Ordinal);
    private readonly object _specificDeliverySignalsGate = new();

    // Count-gated delivery signals: a waiter registers a target invocation
    // count for a handler list; each handler invocation bumps the count and
    // resolves the waiter once the target is reached. Deterministic — no
    // wall-clock timeout, the signal fires exactly when the dispatcher has
    // delivered the awaited event.
    private int _catchAllTarget;
    private TaskCompletionSource? _catchAllSignal;
    private int _specificTarget;
    private TaskCompletionSource? _specificSignal;
    private readonly object _countSignalGate = new();

    private SqliteConnection _keeper = null!;

    public DispatcherFixture()
    {
        DeadLetterStore = new CapturingDeadLetterStore(EventStore);
    }

    public async ValueTask InitializeAsync()
    {
        var dbName = $"mohist-dispatcher-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        // Provision the production schema so the event-store factories in
        // the silo DI have a complete WorkflowRunEvents / IssueEvents /
        // EpicEvents / AgentSessionEvents / DeadLetters set to write into.
        // Without this the producer-side WorkflowRunStore.SaveAsync
        // fails on the first row insert ("no such table").
        MigratedSqliteTemplate.CopyTo(_keeper);

        var builder = new InProcessTestClusterBuilder();
        builder.Options.InitialSilosCount = 2;
        builder.ConfigureClient(clientBuilder =>
            clientBuilder.Services.Configure<ClusterMembershipOptions>(ConfigureTestClusterMembership));
        builder.ConfigureSilo((_, siloBuilder) =>
            ConfigureDispatcherSilo(siloBuilder, connectionString));
        Cluster = builder.Build();
        await Cluster.DeployAsync();
        await Cluster.WaitForLivenessToStabilizeAsync();

        RunnerWorkspace = Cluster.GetSiloServiceProvider(null).GetRequiredService<FakeRunnerWorkspaceClient>();
        EventPublisher.RegisterSink(EventStore);
    }

    public void ResetInvocationRecords()
    {
        EventStore.Reset();
        DeadLetterStore.Reset();
        BackgroundTasks.Reset();
        lock (ClosedGenericInvocations)
            ClosedGenericInvocations.Clear();
        lock (CatchAllInvocations)
            CatchAllInvocations.Clear();
        lock (SpecificInvocations)
            SpecificInvocations.Clear();
        lock (_specificDeliverySignalsGate)
            _specificDeliverySignals.Clear();
        lock (_countSignalGate)
        {
            _catchAllTarget = 0;
            _catchAllSignal = null;
            _specificTarget = 0;
            _specificSignal = null;
        }
    }

    public Task WaitForSpecificInvocationAsync(string eventId)
    {
        lock (_specificDeliverySignalsGate)
        {
            return GetSpecificDeliverySignal(eventId).Task;
        }
    }

    /// <summary>
    /// Returns a task that completes once the catch-all handler has been
    /// invoked enough times to exceed <paramref name="baseline"/>. The
    /// baseline is captured before the producer commits, so the awaited
    /// signal corresponds to the new event's delivery rather than any
    /// earlier one. Deterministic: resolves from the handler's own
    /// invocation, never from a wall-clock timeout.
    /// </summary>
    public Task WaitForCatchAllBeyondAsync(int baseline)
    {
        lock (_countSignalGate)
        {
            _catchAllTarget = baseline + 1;
            _catchAllSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (CatchAllInvocations.Count >= _catchAllTarget)
                _catchAllSignal.TrySetResult();
            return _catchAllSignal.Task;
        }
    }

    /// <summary>
    /// Count-gated counterpart of <see cref="WaitForCatchAllBeyondAsync"/>
    /// for the specific (WorkflowRunCompleted) handler. Used by the poke
    /// specs in place of the per-eventId signal when the producer mints a
    /// fresh envelope id the test cannot predict.
    /// </summary>
    public Task WaitForSpecificBeyondAsync(int baseline)
    {
        lock (_countSignalGate)
        {
            _specificTarget = baseline + 1;
            _specificSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (SpecificInvocations.Count >= _specificTarget)
                _specificSignal.TrySetResult();
            return _specificSignal.Task;
        }
    }

    public void RecordSpecificInvocation(string eventId)
    {
        lock (SpecificInvocations)
            SpecificInvocations.Add(eventId);
        lock (_specificDeliverySignalsGate)
            GetSpecificDeliverySignal(eventId).TrySetResult();
        lock (_countSignalGate)
        {
            if (_specificSignal is not null && SpecificInvocations.Count >= _specificTarget)
                _specificSignal.TrySetResult();
        }
    }

    /// <summary>
    /// Bumps the catch-all count and resolves any
    /// <see cref="WaitForCatchAllBeyondAsync"/> waiter that has reached its
    /// target. Called by <see cref="DispatcherCatchAllHandler"/> on every
    /// delivered event.
    /// </summary>
    public void RecordCatchAllInvocation()
    {
        lock (_countSignalGate)
        {
            if (_catchAllSignal is not null && CatchAllInvocations.Count >= _catchAllTarget)
                _catchAllSignal.TrySetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        Cluster?.Dispose();
        _keeper?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void ConfigureDispatcherSilo(ISiloBuilder siloBuilder, string connectionString)
    {
        siloBuilder.UseInMemoryReminderService();
        siloBuilder.Services.RemoveAll<IReminderTable>();
        siloBuilder.Services.AddSingleton<IReminderTable>(ReminderTable);
        siloBuilder.Configure<ClusterMembershipOptions>(ConfigureTestClusterMembership);
        siloBuilder.Configure<ReminderOptions>(o =>
            o.MinimumReminderPeriod = TimeSpan.FromMilliseconds(100));
        siloBuilder.AddMemoryGrainStorageAsDefault();
        siloBuilder.Services.AddDbContextFactory<MohistDbContext>(o => o.UseSqlite(connectionString));
        siloBuilder.Services.AddScoped<IWorkflowRunStore, WorkflowRunStore>();
        siloBuilder.Services.AddScoped<Mohist.Server.Infrastructure.Data.Issue.IIssueStore, Mohist.Server.Infrastructure.Data.Issue.IssueStore>();
        siloBuilder.Services.AddScoped<Mohist.Server.Infrastructure.Data.Sessions.IAgentSessionStore, Mohist.Server.Infrastructure.Data.Sessions.AgentSessionStore>();
        siloBuilder.Services.AddScoped<IAgentJobStore, AgentJobStore>();
        siloBuilder.Services.AddScoped<RunnerDefinitionStore>();
        siloBuilder.Services.AddScoped<WorkflowRunVariablesStore>();
        siloBuilder.Services.AddScoped<ProjectVariableStore>();
        siloBuilder.Services.AddScoped<IssueVariableStore>();
        siloBuilder.Services.AddSingleton<IPromptLoader>(_ => new FakePromptLoader());
        siloBuilder.Services.AddSingleton<PromptTemplateEngine>();
        siloBuilder.Services.AddScoped<ProjectPromptStore>();
        siloBuilder.Services.AddScoped<WorkflowPromptResolver>();
        siloBuilder.Services.AddSingleton(WorkflowGrainTestHelpers.CreateEmptyConfigService());
        siloBuilder.Services.AddScoped<WorkflowDefinitionResolver>();
        siloBuilder.Services.AddScoped<Mohist.Server.Workflow.Services.WorkflowVariableResolver>();
        siloBuilder.Services.AddScoped<Mohist.Server.Runner.Services.DispatchService>();
        siloBuilder.Services.AddScoped<Mohist.Server.Runner.Services.WorkflowReportService>();
        siloBuilder.Services.AddScoped<WorkflowItemTranslator>();
        siloBuilder.Services.AddScoped<IssueWorkflowProfileRegistry>();
        siloBuilder.Services.AddScoped<EffectiveWorkflowProfileResolver>();
        siloBuilder.Services.AddSingleton<FakeRunnerWorkspaceClient>();
        siloBuilder.Services.AddSingleton<IRunnerWorkspaceClient>(sp => sp.GetRequiredService<FakeRunnerWorkspaceClient>());

        siloBuilder.Services.RemoveAll<IEventStore>();
        siloBuilder.Services.AddSingleton<IEventStore>(EventStore);
        siloBuilder.Services.RemoveAll<IDeadLetterStore>();
        siloBuilder.Services.AddSingleton<IDeadLetterStore>(DeadLetterStore);

        siloBuilder.Services.AddCloudEventBus();
        siloBuilder.Services.AddSingleton(this);
        siloBuilder.Services.AddCloudEventHandlersFromAssembly(typeof(DispatcherFixture).Assembly);

        siloBuilder.Services.AddSingleton<EventDispatcherService>();
        siloBuilder.Services.AddHostedService<DispatcherActivationService>();
        siloBuilder.Services.AddSingleton<TimeProvider>(TimeProvider);
        siloBuilder.Services.Configure<EventDispatcherOptions>(options =>
        {
            options.ReminderPeriod = TimeSpan.FromHours(1);
            options.MaxAttempts = 3;
            options.BaseBackoff = TimeSpan.Zero;
            options.MaxBackoff = TimeSpan.Zero;
        });

        siloBuilder.Services.AddSingleton<ITranscriptEventPublisher, TestNoopTranscriptEventPublisher>();
        siloBuilder.Services.AddScoped<IWorkflowArtifactBindService, WorkflowArtifactBindService>();
        siloBuilder.Services.AddScoped<AgentSessionQuery>();
        siloBuilder.Services.Configure<AgentJobOptions>(opts =>
        {
            opts.DispatchBackoffInitial = TimeSpan.FromMilliseconds(50);
            opts.DispatchBackoffCap = TimeSpan.FromMilliseconds(200);
            opts.DispatchRetryBound = TimeSpan.FromSeconds(5);
            opts.JobTimeout = TimeSpan.FromSeconds(10);
        });
        siloBuilder.Services.AddRequiredInfrastructure();
        siloBuilder.Services.RemoveAll<IBackgroundTaskLauncher>();
        siloBuilder.Services.AddSingleton<IBackgroundTaskLauncher>(BackgroundTasks);
        siloBuilder.Services.Configure<WorkflowOptions>(_ => { });
    }

    private static void ConfigureTestClusterMembership(ClusterMembershipOptions options)
    {
        options.ProbeTimeout = TimeSpan.FromMilliseconds(100);
        options.TableRefreshTimeout = TimeSpan.FromMilliseconds(100);
        options.DeathVoteExpirationTimeout = TimeSpan.FromSeconds(1);
        options.NumProbedSilos = 1;
        options.NumMissedProbesLimit = 1;
        options.NumVotesForDeathDeclaration = 1;
        options.UseLivenessGossip = false;
    }

    private TaskCompletionSource GetSpecificDeliverySignal(string eventId) =>
        _specificDeliverySignals.TryGetValue(eventId, out var signal)
            ? signal
            : _specificDeliverySignals[eventId] = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Minimal <see cref="IEventPublisher"/> that forwards every published
/// envelope to a single sink (the fixture's <see cref="CapturingEventStore"/>).
/// Lets spec tests assert that an event published through the bus is
/// visible to the dispatcher's pull on the next tick.
/// </summary>
public sealed class CapturingEventPublisher : IEventPublisher
{
    private readonly List<CloudEvent> _published = [];
    private IEventStore? _sink;
    private readonly object _gate = new();

    public void RegisterSink(IEventStore sink)
    {
        lock (_gate) { _sink = sink; }
    }

    public IReadOnlyList<CloudEvent> Published
    {
        get { lock (_gate) { return _published.ToList(); } }
    }

    public Task PublishAsync(CloudEvent envelope, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _published.Add(envelope);
            return _sink?.AppendAsync(envelope, ct) ?? Task.CompletedTask;
        }
    }

    public async Task PublishAsync<TData>(
        TData data,
        string type,
        string source,
        string? subject = null,
        IReadOnlyDictionary<string, string>? extensions = null,
        CancellationToken ct = default)
    {
        var dataJson = System.Text.Json.JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions);
        var extDict = extensions is null
            ? null
            : new Dictionary<string, string>(extensions, StringComparer.Ordinal);
        var envelope = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.RelativeOrAbsolute),
            type: type,
            time: DateTimeOffset.UnixEpoch,
            data: dataJson,
            subject: subject,
            extensions: extDict);
        await PublishAsync(envelope, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Closed-generic <see cref="ICloudEventHandler{TData}"/> used by the
/// dispatcher integration specs to assert that the closed-generic
/// discovery fix lands the handler in the fan-out set. Subscribes to
/// the same <c>com.mohist.issue.completed</c> type the production
/// <c>EpicAutoDoneHandler</c> uses; tests publish a matching
/// <see cref="IssueCompleted"/> event and observe this handler being
/// invoked via the dispatcher's pull–fan-out cycle.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.IssueCompleted)]
public sealed class DispatcherClosedGenericHandler : ICloudEventHandler<IssueCompleted>
{
    private readonly DispatcherFixture _fixture;

    public DispatcherClosedGenericHandler(DispatcherFixture fixture) => _fixture = fixture;

    public bool Filter(CloudEvent<IssueCompleted> evt) => true;

    public Task HandleAsync(CloudEvent<IssueCompleted> evt, CancellationToken ct)
    {
        lock (_fixture.ClosedGenericInvocations)
        {
            _fixture.ClosedGenericInvocations.Add(evt.Id);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Catch-all subscription used to assert the wildcard type matcher
/// ("*") still receives every event the dispatcher pulls. The
/// pattern; this test handler is its pure-DI stand-in.
/// </summary>
[Subscription(Type = "*")]
public sealed class DispatcherCatchAllHandler : ICloudEventHandler
{
    private readonly DispatcherFixture _fixture;

    public DispatcherCatchAllHandler(DispatcherFixture fixture) => _fixture = fixture;

    public bool Filter(CloudEvent evt) => true;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        lock (_fixture.CatchAllInvocations)
        {
            _fixture.CatchAllInvocations.Add(evt.Id);
        }
        _fixture.RecordCatchAllInvocation();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Concrete-type subscription used to assert the non-generic path
/// (a handler implementing <see cref="ICloudEventHandler"/> directly)
/// also receives the dispatched event alongside the closed-generic
/// handler when both subscribe to the same type.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.WorkflowRunCompleted)]
public sealed class DispatcherSpecificHandler : ICloudEventHandler
{
    private readonly DispatcherFixture _fixture;

    public DispatcherSpecificHandler(DispatcherFixture fixture) => _fixture = fixture;

    public bool Filter(CloudEvent evt) => true;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        _fixture.RecordSpecificInvocation(evt.Id);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Local stand-in for the GrainTestConfig.NoopTranscriptEventPublisher —
/// the dispatcher fixture doesn't pull in the larger workflow test
/// infrastructure, so it ships its own transcript sink.
/// </summary>
public sealed class TestNoopTranscriptEventPublisher : ITranscriptEventPublisher
{
    public Task PublishAsync(TranscriptEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Poison-message handler used by the dead-letter spec test to exercise
/// the grain → service → dead-letter wiring. Subscribes to a test-only
/// type and always throws, forcing retry exhaustion and dead-lettering.
/// </summary>
[Subscription(Type = "test.poison")]
public sealed class DispatcherPoisonHandler : ICloudEventHandler
{
    public bool Filter(CloudEvent evt) => true;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) =>
        throw new InvalidOperationException("poison test handler");
}
