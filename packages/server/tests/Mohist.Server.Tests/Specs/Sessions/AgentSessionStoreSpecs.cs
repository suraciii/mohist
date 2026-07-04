using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly AgentSessionStore _store;
    private readonly AgentSessionTranscriptStore _transcriptStore;
    private readonly SqliteConnection _keeper;

    public AgentSessionStoreSpecs()
    {
        var connectionString = $"Data Source=agent-session-store-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .Options;
        _store = new AgentSessionStore(new Factory(_options));
        _transcriptStore = new AgentSessionTranscriptStore(new Factory(_options));

        using var db = new MohistDbContext(_options);
        db.Database.EnsureCreated();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
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
}
