using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Orleans.Configuration;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public sealed class AgentSessionGrainFixture : IAsyncLifetime
{
    public InProcessTestCluster Cluster { get; private set; } = null!;
    public IGrainFactory Grains => Cluster.Client;
    public FakeAgentSessionStore StateStore { get; } = new();
    public FakeAgentSessionTranscriptStore TranscriptStore { get; } = new();
    public RecordingTranscriptEventPublisher TranscriptPublisher { get; } = new();
    public AgentSessionPersistenceTestProbe Persistence { get; }
    public TestLogger<AgentSessionGrain> Logger { get; } = new();
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public string ConnectionString { get; private set; } = null!;

    public MohistDbContext CreateDbContext() => _database.CreateContext();

    public void Reset()
    {
        StateStore.Reset();
        TranscriptStore.Reset();
        TranscriptPublisher.Clear();
        Logger.Entries.Clear();
    }

    private TestSqliteDatabase _database = null!;
    public AgentSessionGrainFixture()
    {
        Persistence = new AgentSessionPersistenceTestProbe(
            () => TimeProvider.Advance(TimeSpan.FromSeconds(1)));
    }

    public ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        ConnectionString = _database.ConnectionString;

        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.UseInMemoryReminderService();
            siloBuilder.AddMemoryGrainStorageAsDefault();
            siloBuilder.Configure<GrainCollectionOptions>(options => options.CollectionAge = TimeSpan.FromMinutes(10));
            siloBuilder.Services.AddDbContextFactory<MohistDbContext>(options => options.UseSqlite(ConnectionString));

            siloBuilder.Services.AddSingleton<IAgentSessionStore>(StateStore);
            siloBuilder.Services.AddSingleton<IAgentSessionTranscriptStore>(TranscriptStore);
            siloBuilder.Services.AddSingleton<ITranscriptEventPublisher>(TranscriptPublisher);
            siloBuilder.Services.AddSingleton<IAgentSessionPersistenceObserver>(Persistence);
             siloBuilder.Services.AddSingleton<TimeProvider>(TimeProvider);
             siloBuilder.Services.AddSingleton<RunnerConnectionTracker>();
             siloBuilder.Services.AddSingleton<IAgentSessionConnectionRegistry>(sp =>
                 sp.GetRequiredService<RunnerConnectionTracker>());
            siloBuilder.Services.AddSingleton<ILogger<AgentSessionGrain>>(Logger);
        });
        Cluster = builder.Build();
        return new ValueTask(Cluster.DeployAsync());
    }

    public ValueTask DisposeAsync()
    {
        Cluster?.Dispose();
        _database?.Dispose();
        return ValueTask.CompletedTask;
    }

    public sealed class RecordingTranscriptEventPublisher : ITranscriptEventPublisher
    {
        public List<TranscriptEnvelope> Published { get; } = [];

        public void Clear() => Published.Clear();

        public Task PublishAsync(TranscriptEnvelope envelope, CancellationToken ct = default)
        {
            Published.Add(envelope);
            return Task.CompletedTask;
        }
    }
}

    public sealed class FakeAgentSessionStore : IAgentSessionStore
    {
        // State is keyed by session so a lingering grain (the test cluster is
        // shared across tests) flushing on its real-time persist timer cannot
        // clobber another session's persisted state and break reactivation.
        private readonly Dictionary<string, AgentSession> _states = new(StringComparer.Ordinal);
        private string? _lastSavedKey;

        // Most-recently-saved state, for synchronous test assertions. Kept as
        // last-write-wins to preserve the existing single-slot semantics.
        public AgentSession? State =>
            _lastSavedKey is not null && _states.TryGetValue(_lastSavedKey, out var state) ? state : null;

        public List<AgentSessionEvent> Events { get; } = [];
        public int SaveCount { get; private set; }
        private (string Key, Exception Error)? _nextFailure;
        private string? _commitThenThrowNextKey;

        public void FailNextSave(string key, Exception error) => _nextFailure = (key, error);

        public void CommitThenThrowNextSave(string key) => _commitThenThrowNextKey = key;

        public void Reset()
        {
            _nextFailure = null;
            SaveCount = 0;
            _states.Clear();
            _lastSavedKey = null;
            Events.Clear();
            _commitThenThrowNextKey = null;
        }

        public bool Contains(string key) => _states.ContainsKey(key);

        public Task<AgentSession?> LoadAsync(string key) =>
            Task.FromResult(_states.TryGetValue(key, out var state) ? Clone(state) : null);

        public Task<IReadOnlyList<AgentSession>> ListAsync() =>
            Task.FromResult<IReadOnlyList<AgentSession>>(_states.Values.Select(Clone).ToArray());

        public Task<IReadOnlyList<AgentSessionReconcileBinding>> ListByRunnerForReconcileAsync(
            string runnerId,
            CancellationToken ct = default)
        {
            var matches = new List<AgentSessionReconcileBinding>();
            foreach (var state in _states.Values)
            {
                if (!string.Equals(state.Runtime.RunnerId, runnerId, StringComparison.Ordinal)
                    || state.Status.Activity == AgentSessionActivity.Idle
                    || string.IsNullOrWhiteSpace(state.Runtime.Runtime)
                    || string.IsNullOrWhiteSpace(state.Status.AgentRuntimeSessionId)
                    || string.IsNullOrWhiteSpace(state.Runtime.WorkDir))
                    continue;

                matches.Add(new AgentSessionReconcileBinding(
                    state.Id,
                    state.Runtime.Runtime!,
                    state.Status.AgentRuntimeSessionId,
                    state.Runtime.WorkDir));
            }

            return Task.FromResult<IReadOnlyList<AgentSessionReconcileBinding>>(matches);
        }

        public Task SaveAsync(string key, AgentSession state)
        {
            ThrowIfPending(key);
            SaveCount++;
            _states[key] = Clone(state);
            _lastSavedKey = key;
            return Task.CompletedTask;
        }

        public Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, CancellationToken ct = default)
        {
            var commitThenThrow = string.Equals(_commitThenThrowNextKey, key, StringComparison.Ordinal);
            if (!commitThenThrow)
                ThrowIfPending(key);
            SaveCount++;
            _states[key] = Clone(state);
            _lastSavedKey = key;
            Events.AddRange(events);
            if (commitThenThrow)
            {
                _commitThenThrowNextKey = null;
                throw new InvalidOperationException("store committed before transport failure");
            }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key)
        {
            _states.Remove(key);
            if (string.Equals(_lastSavedKey, key, StringComparison.Ordinal))
                _lastSavedKey = null;
            return Task.CompletedTask;
        }

        private void ThrowIfPending(string key)
        {
            if (_nextFailure is not { } failure ||
                !string.Equals(failure.Key, key, StringComparison.Ordinal))
                return;

            _nextFailure = null;
            throw failure.Error;
        }

        private static AgentSession Clone(AgentSession state) =>
            JSON.Deserialize<AgentSession>(JSON.Serialize(state))
            ?? throw new InvalidOperationException("Failed to clone AgentSession state.");
    }

public sealed class FakeAgentSessionTranscriptStore : IAgentSessionTranscriptStore
{
    public List<AgentSessionTranscriptFlush> Flushes { get; } = [];
    private (string SessionId, Exception Error)? _nextFailure;
    private readonly object _gate = new();
    private readonly List<PendingFlushWait> _waiters = [];

    public void FailNextSave(string sessionId, Exception error) => _nextFailure = (sessionId, error);

    public void Reset()
    {
        lock (_gate)
        {
            _nextFailure = null;
            Flushes.Clear();
            foreach (var waiter in _waiters)
                waiter.Completion.TrySetCanceled();
            _waiters.Clear();
        }
    }

    public Task SaveAsync(AgentSessionTranscriptFlush transcript, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_nextFailure is { } failure &&
                string.Equals(failure.SessionId, transcript.Turn.SessionId, StringComparison.Ordinal))
            {
                _nextFailure = null;
                throw failure.Error;
            }

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
        return Task.CompletedTask;
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

public sealed class TestLogger<T> : ILogger<T>
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
