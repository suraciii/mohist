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
    public TestLogger<AgentSessionGrain> Logger { get; } = new();
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public string ConnectionString { get; private set; } = null!;

    public void Reset()
    {
        StateStore.Reset();
        TranscriptStore.Reset();
        TranscriptPublisher.Clear();
        Logger.Entries.Clear();
    }

    private TestSqliteDatabase _database = null!;

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
        public AgentSession? State { get; private set; }
        public List<AgentSessionEvent> Events { get; } = [];
        public int SaveCount { get; private set; }
        public Exception? NextException { get; set; }
        public bool CommitThenThrowNext { get; set; }

        public void Reset()
        {
            NextException = null;
            SaveCount = 0;
            State = null;
            Events.Clear();
            CommitThenThrowNext = false;
        }

        public Task<AgentSession?> LoadAsync(string key) => Task.FromResult(State is null ? null : Clone(State));

        public Task<IReadOnlyList<AgentSession>> ListAsync() =>
            Task.FromResult<IReadOnlyList<AgentSession>>(State is null ? [] : [Clone(State)]);

        public Task SaveAsync(string key, AgentSession state)
        {
            ThrowIfPending();
            SaveCount++;
            State = Clone(state);
            return Task.CompletedTask;
        }

        public Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, CancellationToken ct = default)
        {
            if (!CommitThenThrowNext)
                ThrowIfPending();
            SaveCount++;
            State = Clone(state);
            Events.AddRange(events);
            if (CommitThenThrowNext)
            {
                CommitThenThrowNext = false;
                throw new InvalidOperationException("store committed before transport failure");
            }
            return Task.CompletedTask;
        }

    public Task DeleteAsync(string key)
    {
        State = null;
        return Task.CompletedTask;
    }

    private void ThrowIfPending()
    {
        if (NextException is null) return;
        var ex = NextException;
        NextException = null;
        throw ex;
    }

    private static AgentSession Clone(AgentSession state) =>
        JSON.Deserialize<AgentSession>(JSON.Serialize(state))
        ?? throw new InvalidOperationException("Failed to clone AgentSession state.");
}

public sealed class FakeAgentSessionTranscriptStore : IAgentSessionTranscriptStore
{
    public List<AgentSessionTranscriptFlush> Flushes { get; } = [];
    public Exception? NextException { get; set; }

    public void Reset()
    {
        NextException = null;
        Flushes.Clear();
    }

    public Task SaveAsync(AgentSessionTranscriptFlush transcript, CancellationToken ct = default)
    {
        if (NextException is not null)
        {
            var ex = NextException;
            NextException = null;
            throw ex;
        }

        Flushes.Add(transcript);
        return Task.CompletedTask;
    }
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
