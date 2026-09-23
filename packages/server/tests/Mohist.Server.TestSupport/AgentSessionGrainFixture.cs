using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Capacity;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Orleans.Configuration;
using Orleans.Reminders;
using Orleans.TestingHost;
using Xunit;
using AgentDomain = Mohist.Server.Agent.Domain.Agent;
using AgentStatusDomain = Mohist.Server.Agent.Domain.AgentStatus;

namespace Mohist.Server.TestSupport;

public sealed class AgentSessionGrainFixture : IAsyncLifetime
{
    public InProcessTestCluster Cluster { get; private set; } = null!;
    public IGrainFactory Grains => Cluster.Client;
    public ObservableAgentSessionStore StateStore { get; private set; } = null!;
    public FakeAgentSessionTranscriptStore TranscriptStore { get; private set; } = null!;
    public RecordingTranscriptEventPublisher TranscriptPublisher { get; private set; } = null!;
    public RecordingFollowupDispatchScheduler FollowupDispatch { get; private set; } = null!;
    public AgentSessionPersistenceTestProbe Persistence { get; private set; } = null!;
    public AgentSessionGrainTestLogger<AgentSessionGrain> Logger { get; } = new();
    public FakeTimeProvider TimeProvider { get; } = new(TestTime.UtcNow);
    public string ConnectionString { get; private set; } = null!;
    public IServiceProvider SiloServices => Cluster.GetSiloServiceProvider(null);

    public MohistDbContext CreateDbContext() => new(_dbOptions);

    /// <summary>
    /// Seeds a real Project Agent definition so the derived capacity store
    /// can read the live limit and identity for the sessions a successful
    /// spec dispatches. Negative specs deliberately leave the definition
    /// absent.
    /// </summary>
    public async Task SeedAgentAsync(string projectId, string agentId, int? maxConcurrentRuns)
    {
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        var agent = new AgentDomain
        {
            Id = agentId,
            ProjectId = projectId,
            Name = $"agent-{agentId}",
            Description = "spec",
            Instructions = "spec",
            Skills = Array.Empty<string>(),
            MaxConcurrentRuns = maxConcurrentRuns,
            Status = AgentStatusDomain.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var rowId = GrainKey.Agent(projectId, agentId);
        await using var db = CreateDbContext();
        var existing = await db.Agents.FindAsync(rowId);
        if (existing is null)
        {
            db.Agents.Add(new AgentRow
            {
                Id = rowId,
                ProjectId = projectId,
                Name = agent.Name,
                Status = agent.Status,
                State = AgentStore.Serialize(agent),
            });
        }
        else
        {
            existing.ProjectId = projectId;
            existing.Name = agent.Name;
            existing.Status = agent.Status;
            existing.State = AgentStore.Serialize(agent);
        }
        await db.SaveChangesAsync();
    }

    public void Reset()
    {
        StateStore.Reset();
        TranscriptStore.Reset();
        TranscriptPublisher.Clear();
        FollowupDispatch.Reset();
        Logger.Entries.Clear();
    }

    private SqliteConnection _keeper = null!;
    private DbContextOptions<MohistDbContext> _dbOptions = null!;

    public ValueTask InitializeAsync()
    {
        ConnectionString = $"Data Source=agent-session-grain-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(ConnectionString);
        _keeper.Open();
        MigratedSqliteTemplate.CopyTo(_keeper);
        _dbOptions = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        // The real Session store owns the persisted document in the fixture's
        // own hermetic database, so a capacity claim compares and patches the
        // same document the grain saves; the decorator only observes, fences
        // stale scenarios, and injects failures.
        var eventStore = new NoopEventStore();
        var dispatchSignal = new Mohist.Server.Infrastructure.Events.EventDispatchSignal();
        StateStore = new ObservableAgentSessionStore(
            new AgentSessionStore(
                new TestDbContextFactory(_dbOptions),
                eventStore,
                NullLogger<AgentSessionStore>.Instance,
                dispatchSignal),
            _dbOptions);
        Persistence = new AgentSessionPersistenceTestProbe(
            () => TimeProvider.Advance(TimeSpan.FromSeconds(1)));

        TranscriptStore = new FakeAgentSessionTranscriptStore(StateStore.IsCurrentScenario);
        TranscriptPublisher = new RecordingTranscriptEventPublisher(StateStore.IsCurrentScenario);
        FollowupDispatch = new RecordingFollowupDispatchScheduler(StateStore.IsCurrentScenario);

        var builder = new InProcessTestClusterBuilder().UseLogicalPorts();
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.UseInMemoryReminderService();
            siloBuilder.AddMemoryGrainStorageAsDefault();
            siloBuilder.Configure<GrainCollectionOptions>(options => options.CollectionAge = TimeSpan.FromMinutes(10));
            siloBuilder.Configure<ReminderOptions>(options =>
                options.MinimumReminderPeriod = TimeSpan.FromMilliseconds(100));
            siloBuilder.Services.AddDbContextFactory<MohistDbContext>(options => options.UseSqlite(ConnectionString));
            siloBuilder.Services.AddSingleton(new Mohist.Server.Infrastructure.Events.EventDispatchSignal());
            // The derived capacity store is the follow-up admission authority;
            // it reads the same hermetic database the real Session store writes.
            siloBuilder.Services.AddScoped<IAgentCapacityStore, AgentCapacityStore>();

            siloBuilder.Services.AddSingleton<IAgentSessionStore>(StateStore);
            siloBuilder.Services.AddSingleton<IAgentSessionTranscriptStore>(TranscriptStore);
            siloBuilder.Services.AddSingleton<ITranscriptEventPublisher>(TranscriptPublisher);
            siloBuilder.Services.AddSingleton<IFollowupDispatchScheduler>(FollowupDispatch);
            siloBuilder.Services.AddSingleton<IAgentSessionPersistenceObserver>(Persistence);
            siloBuilder.Services.AddSingleton<TimeProvider>(TimeProvider);
            siloBuilder.Services.AddSingleton<RunnerConnectionTracker>();
            siloBuilder.Services.AddSingleton<IAgentSessionConnectionRegistry>(sp =>
                sp.GetRequiredService<RunnerConnectionTracker>());
            siloBuilder.Services.AddSingleton<ILogger<AgentSessionGrain>>(Logger);
            siloBuilder.Services.AddSingleton<IEventStore>(new NoopEventStore());
            siloBuilder.Services.AddSingleton<IBackgroundTaskLauncher, BackgroundTaskLauncher>();
            siloBuilder.Services.AddScoped<AgentQuerier>();
            siloBuilder.Services.AddScoped<AgentJobQuerier>();
        });
        Cluster = builder.Build();
        return new ValueTask(Cluster.DeployAsync());
    }

    public ValueTask DisposeAsync()
    {
        Cluster?.Dispose();
        _keeper?.Dispose();
        return ValueTask.CompletedTask;
    }

    public sealed class RecordingTranscriptEventPublisher : ITranscriptEventPublisher
    {
        private readonly Func<string, bool> _isCurrentScenario;

        public RecordingTranscriptEventPublisher(Func<string, bool> isCurrentScenario)
        {
            _isCurrentScenario = isCurrentScenario;
        }

        public List<TranscriptEnvelope> Published { get; } = [];
        public Action<TranscriptEnvelope>? BeforePublish { get; set; }

        public void Clear()
        {
            Published.Clear();
            BeforePublish = null;
        }

        public Task PublishAsync(string projectId, TranscriptEnvelope envelope, CancellationToken ct = default)
        {
            if (_isCurrentScenario(envelope.SessionId))
            {
                BeforePublish?.Invoke(envelope);
                Published.Add(envelope);
            }
            return Task.CompletedTask;
        }
    }


}
public sealed class FakeAgentSessionTranscriptStore : IAgentSessionTranscriptStore
{
    private readonly Func<string, bool> _isCurrentScenario;
    public List<AgentSessionTranscriptFlush> Flushes { get; } = [];
    public Func<AgentSessionTranscriptFlush, Task>? BeforeSaveAsync { get; set; }
    private (string SessionId, Exception Error)? _nextFailure;
    private readonly object _gate = new();
    private readonly List<PendingFlushWait> _waiters = [];

    public FakeAgentSessionTranscriptStore(Func<string, bool> isCurrentScenario)
    {
        _isCurrentScenario = isCurrentScenario;
    }

    public void FailNextSave(string sessionId, Exception error) => _nextFailure = (sessionId, error);

    public void Reset()
    {
        lock (_gate)
        {
            _nextFailure = null;
            BeforeSaveAsync = null;
            Flushes.Clear();
            foreach (var waiter in _waiters)
                waiter.Completion.TrySetCanceled();
            _waiters.Clear();
        }
    }

    public async Task SaveAsync(AgentSessionTranscriptFlush transcript, CancellationToken ct = default)
    {
        if (_isCurrentScenario(transcript.Turn.SessionId) && BeforeSaveAsync is { } beforeSave)
            await beforeSave(transcript);
        lock (_gate)
        {
            if (_nextFailure is { } failure &&
                string.Equals(failure.SessionId, transcript.Turn.SessionId, StringComparison.Ordinal))
            {
                _nextFailure = null;
                throw failure.Error;
            }

            if (!_isCurrentScenario(transcript.Turn.SessionId))
                return;

            Flushes.Add(transcript);
            for (var index = _waiters.Count - 1; index >= 0; index--)
            {
                var waiter = _waiters[index];
                if (!waiter.Predicate(transcript))
                    continue;

                _waiters.RemoveAt(index);
                waiter.Completion.TrySetResult(transcript);
            }
        }
    }

    public Task<AgentSessionTranscriptFlush> WaitForAsync(Func<AgentSessionTranscriptFlush, bool> predicate)
    {
        lock (_gate)
        {
            var existing = Flushes.LastOrDefault(predicate);
            if (existing is not null)
                return Task.FromResult(existing);

            var completion = new TaskCompletionSource<AgentSessionTranscriptFlush>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(new PendingFlushWait(predicate, completion));
            return completion.Task;
        }
    }

    private sealed record PendingFlushWait(
        Func<AgentSessionTranscriptFlush, bool> Predicate,
        TaskCompletionSource<AgentSessionTranscriptFlush> Completion);
}

public sealed class AgentSessionGrainTestLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var structuredState = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception, structuredState));
    }
}

public sealed record LogEntry(
    LogLevel Level,
    string Message,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> State);

/// <summary>
/// Observable, fault-injectable decorator over the real
/// <see cref="AgentSessionStore"/> in the fixture's own hermetic migrated
/// database. The real store stays the only owner of the persisted document, so
/// an atomic capacity claim compares and patches the same State the grain
/// saves; there is no in-memory mirror that a direct capacity commit could
/// leave stale.
///
/// The cluster is shared across specs, so a session belongs to the scenario
/// that first wrote it. A write from an earlier scenario — a lingering grain
/// flushing on its persist timer after its spec finished — is fenced before it
/// reaches SQLite, and <see cref="Reset"/> removes that scenario's rows so one
/// spec's queued or claimed work can never occupy another spec's capacity
/// view. Observation probes (save count, events, last saved state) stay
/// scenario-scoped exactly as the previous fake scoped them.
/// </summary>
public sealed class ObservableAgentSessionStore : IAgentSessionStore
{
    private const int CleanupChunkSize = 400;

    private readonly IAgentSessionStore _inner;
    private readonly DbContextOptions<MohistDbContext> _dbOptions;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _scenarioBySession = new(StringComparer.Ordinal);
    private long _scenario;
    private string? _lastSavedKey;
    private AgentSession? _lastSavedState;
    private (string Key, Exception Error)? _nextFailure;
    private string? _commitThenThrowNextKey;

    public ObservableAgentSessionStore(
        IAgentSessionStore inner,
        DbContextOptions<MohistDbContext> dbOptions)
    {
        _inner = inner;
        _dbOptions = dbOptions;
        IsCurrentScenario = IsCurrentScenarioCore;
    }

    public Func<string, bool> IsCurrentScenario { get; private set; }

    // Most-recently-saved state, for synchronous test assertions. Single-slot
    // last-write-wins, scoped to the running scenario.
    public AgentSession? State
    {
        get
        {
            lock (_gate)
                return _lastSavedKey is not null ? _lastSavedState : null;
        }
    }

    public List<AgentSessionEvent> Events { get; } = [];
    public int SaveCount { get; private set; }
    public Func<string, Task>? BeforeSaveAsync { get; set; }

    public void FailNextSave(string key, Exception error)
    {
        lock (_gate)
            _nextFailure = (key, error);
    }

    public void CommitThenThrowNextSave(string key)
    {
        lock (_gate)
            _commitThenThrowNextKey = key;
    }

    public async Task<AgentSession?> LoadAsync(string key) => await _inner.LoadAsync(key);

    public Task<IReadOnlyList<AgentSession>> ListAsync() => _inner.ListAsync();

    public Task<IReadOnlyList<AgentSessionReconcileBinding>> ListByRunnerForReconcileAsync(
        string runnerId,
        CancellationToken ct = default) =>
        _inner.ListByRunnerForReconcileAsync(runnerId, ct);

    public async Task<string?> ReadStateJsonAsync(string key, CancellationToken ct = default) =>
        await _inner.ReadStateJsonAsync(key, ct);

    public async Task SaveAsync(string key, AgentSession state)
    {
        if (!ObserveSaveKey(key))
            return;
        ThrowIfPending(key);
        await _inner.SaveAsync(key, state);
        ObserveSave(key, state, null);
    }

    public async Task SaveAsync(
        string key,
        AgentSession state,
        IReadOnlyList<AgentSessionEvent> events,
        CancellationToken ct = default)
    {
        if (!ObserveSaveKey(key))
            return;
        if (IsCurrentScenario(key) && BeforeSaveAsync is { } beforeSave)
            await beforeSave(key);
        var commitThenThrow = IsCommitThenThrow(key);
        if (!commitThenThrow)
            ThrowIfPending(key);
        await _inner.SaveAsync(key, state, events, ct);
        ObserveSave(key, state, events);
        if (commitThenThrow)
        {
            ConsumeCommitThenThrow(key);
            throw new InvalidOperationException("store committed before transport failure");
        }
    }

    public Task DeleteAsync(string key) => _inner.DeleteAsync(key);

    public void Reset()
    {
        string[] staleKeys;
        lock (_gate)
        {
            _scenario++;
            staleKeys = _scenarioBySession
                .Where(entry => entry.Value != _scenario)
                .Select(entry => entry.Key)
                .ToArray();
            _nextFailure = null;
            _commitThenThrowNextKey = null;
            SaveCount = 0;
            _lastSavedKey = null;
            _lastSavedState = null;
            Events.Clear();
            BeforeSaveAsync = null;
            IsCurrentScenario = IsCurrentScenarioCore;
        }

        DeleteScenarioRows(staleKeys);
    }

    private bool IsCurrentScenarioCore(string key)
    {
        lock (_gate)
            return _scenarioBySession.TryGetValue(key, out var scenario)
                && scenario == _scenario;
    }

    /// <summary>
    /// Records the writing scenario and reports whether the write belongs to
    /// it. A session first written by an earlier scenario is fenced here, so a
    /// lingering grain cannot resurrect its row over the current spec.
    /// </summary>
    private bool ObserveSaveKey(string key)
    {
        lock (_gate)
        {
            if (!_scenarioBySession.TryGetValue(key, out var scenario))
            {
                scenario = _scenario;
                _scenarioBySession[key] = scenario;
            }

            return scenario == _scenario;
        }
    }

    private void ObserveSave(string key, AgentSession state, IReadOnlyList<AgentSessionEvent>? events)
    {
        lock (_gate)
        {
            SaveCount++;
            _lastSavedKey = key;
            _lastSavedState = Clone(state);
            if (events is not null)
                Events.AddRange(events);
        }
    }

    private void ThrowIfPending(string key)
    {
        (string Key, Exception Error)? failure;
        lock (_gate)
        {
            failure = _nextFailure;
            if (failure is null || !string.Equals(failure.Value.Key, key, StringComparison.Ordinal))
                return;
            _nextFailure = null;
        }

        throw failure.Value.Error;
    }

    private bool IsCommitThenThrow(string key)
    {
        lock (_gate)
            return string.Equals(_commitThenThrowNextKey, key, StringComparison.Ordinal);
    }

    private void ConsumeCommitThenThrow(string key)
    {
        lock (_gate)
        {
            if (string.Equals(_commitThenThrowNextKey, key, StringComparison.Ordinal))
                _commitThenThrowNextKey = null;
        }
    }

    private void DeleteScenarioRows(string[] keys)
    {
        if (keys.Length == 0)
            return;
        using var db = new MohistDbContext(_dbOptions);
        db.Database.OpenConnection();
        foreach (var chunk in keys.Chunk(CleanupChunkSize))
        {
            db.AgentSessionLifecycleTransitions
                .Where(row => chunk.Contains(row.SessionId))
                .ExecuteDelete();
            db.AgentSessions
                .Where(row => chunk.Contains(row.Id))
                .ExecuteDelete();
        }
    }

    private static AgentSession Clone(AgentSession state) =>
        JSON.Deserialize<AgentSession>(JSON.Serialize(state))
        ?? throw new InvalidOperationException("Failed to clone AgentSession state.");
}
