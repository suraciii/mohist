using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Support.TestData;
using Xunit;

namespace Mohist.Server.Tests.Specs.Sessions;

[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
public class AgentSessionStoreSpecs : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly CollectingLogger<AgentSessionStore> _logger;
    private readonly AgentSessionStore _store;
    private readonly AgentSessionTranscriptStore _transcriptStore;

    public AgentSessionStoreSpecs()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"agent-session-store-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _logger = new CollectingLogger<AgentSessionStore>();
        _store = new AgentSessionStore(new Factory(_options), _logger);
        _transcriptStore = new AgentSessionTranscriptStore(new Factory(_options));

        using var db = new MohistDbContext(_options);
        db.Database.EnsureCreated();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = new MohistDbContext(_options);
        await db.Database.EnsureDeletedAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task SaveAsync_NullLabels_LogsWarningAndRemovesExistingLabels()
    {
        var (session, _) = AgentSessionTestData.CreateRunning();
        await _store.SaveAsync(session.Id, session);

        session.Metadata = new AgentSessionMetadata();
        await _store.SaveAsync(session.Id, session);

        var warning = Assert.Single(_logger.Entries, e => e.LogLevel == LogLevel.Warning);
        Assert.Contains(session.Id, warning.Message);

        await using var db = new MohistDbContext(_options);
        var remaining = await db.AgentSessionLabels
            .Where(label => label.SessionId == session.Id)
            .ToListAsync();
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task SavePartsAsync_RetrySameCorrelationKey_UpdatesExistingPart()
    {
        var sessionId = $"transcript-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        var turn = new AgentSessionTranscriptTurnUpsert(sessionId, 1, "prompt", "task", now, now);
        var parts = new[]
        {
            new AgentSessionTranscriptPartDelta("text", "msg-1", null, "hello", "{}", now, now, 1)
        };

        await _transcriptStore.SaveAsync(new AgentSessionTranscriptFlush(true, turn, parts));

        var retryParts = new[]
        {
            new AgentSessionTranscriptPartDelta("text", "msg-1", null, " world", "{}", now, now, 1)
        };
        await _transcriptStore.SaveAsync(new AgentSessionTranscriptFlush(false, turn, retryParts));

        await using var db = new MohistDbContext(_options);
        var partRows = await db.AgentSessionTranscriptParts.ToListAsync();
        var part = Assert.Single(partRows);
        Assert.Equal("hello world", part.Text);
    }

    [Fact]
    public async Task SavePartsAsync_NewCorrelationKey_InsertsAdditionalPart()
    {
        var sessionId = $"transcript-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        var turn = new AgentSessionTranscriptTurnUpsert(sessionId, 1, "prompt", "task", now, now);
        var firstParts = new[]
        {
            new AgentSessionTranscriptPartDelta("text", "msg-1", null, "hello", "{}", now, now, 1)
        };
        await _transcriptStore.SaveAsync(new AgentSessionTranscriptFlush(true, turn, firstParts));

        var secondParts = new[]
        {
            new AgentSessionTranscriptPartDelta("text", "msg-2", null, "world", "{}", now, now, 1)
        };
        await _transcriptStore.SaveAsync(new AgentSessionTranscriptFlush(false, turn, secondParts));

        await using var db = new MohistDbContext(_options);
        var partRows = await db.AgentSessionTranscriptParts.OrderBy(p => p.Sequence).ToListAsync();
        Assert.Equal(2, partRows.Count);
        Assert.Equal("msg-1", partRows[0].CorrelationKey);
        Assert.Equal("msg-2", partRows[1].CorrelationKey);
    }

    private sealed class Factory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public Factory(DbContextOptions<MohistDbContext> options) => _options = options;

        public MohistDbContext CreateDbContext() => new(_options);
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel LogLevel, string Message, Exception? Exception);
}
