using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
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
    public TestLogger<AgentSessionGrain> Logger { get; } = new();
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public string ConnectionString { get; private set; } = null!;

    public void Reset()
    {
        StateStore.Reset();
        TranscriptStore.Reset();
        Logger.Entries.Clear();
    }

    private SqliteConnection _keeper = null!;

    public Task InitializeAsync()
    {
        var dbName = $"mohist-agent-session-test-{Guid.NewGuid():N}";
        ConnectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(ConnectionString);
        _keeper.Open();

        MigratedSqliteTemplate.CopyTo(_keeper);

        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.UseInMemoryReminderService();
            siloBuilder.AddMemoryGrainStorageAsDefault();
            siloBuilder.Configure<GrainCollectionOptions>(options => options.CollectionAge = TimeSpan.FromMinutes(10));
            siloBuilder.Services.AddDbContextFactory<MohistDbContext>(options => options.UseSqlite(ConnectionString));

            siloBuilder.Services.AddSingleton<IAgentSessionStore>(StateStore);
            siloBuilder.Services.AddSingleton<IAgentSessionTranscriptStore>(TranscriptStore);
            siloBuilder.Services.AddSingleton<ITranscriptEventPublisher>(new NoopTranscriptEventPublisher());
            siloBuilder.Services.AddSingleton<TimeProvider>(TimeProvider);
            siloBuilder.Services.AddSingleton<ILogger<AgentSessionGrain>>(Logger);
        });
        Cluster = builder.Build();
        return Cluster.DeployAsync();
    }

    public Task DisposeAsync()
    {
        Cluster?.Dispose();
        _keeper?.Dispose();
        return Task.CompletedTask;
    }

    private sealed class NoopTranscriptEventPublisher : ITranscriptEventPublisher
    {
        public Task PublishAsync(TranscriptEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}

    public sealed class FakeAgentSessionStore : IAgentSessionStore
    {
        public AgentSession? State { get; private set; }
        public List<AgentSessionEvent> Events { get; } = [];
        public int SaveCount { get; private set; }
        public Exception? NextException { get; set; }

        public void Reset()
        {
            NextException = null;
            SaveCount = 0;
            State = null;
            Events.Clear();
        }

        public Task<AgentSession?> LoadAsync(string key) => Task.FromResult(State);

        public Task<IReadOnlyList<AgentSession>> ListAsync() =>
            Task.FromResult<IReadOnlyList<AgentSession>>(State is null ? [] : [State]);

        public Task SaveAsync(string key, AgentSession state)
        {
            ThrowIfPending();
            SaveCount++;
            State = state;
            return Task.CompletedTask;
        }

        public Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, CancellationToken ct = default)
        {
            ThrowIfPending();
            SaveCount++;
            State = state;
            Events.AddRange(events);
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
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}

public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
