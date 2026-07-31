using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public class SlackDmSessionMappingStoreTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextNow = new(2026, 7, 31, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetCurrentSessionIdAsync_ReturnsNullWhenNoMappingExists()
    {
        await using var harness = CreateHarness();
        var sessionId = await harness.Store.GetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "D-nothing");
        Assert.Null(sessionId);
    }

    [Fact]
    public async Task SetCurrentSessionIdAsync_ThenGet_RoundTripsValue()
    {
        await using var harness = CreateHarness();
        await harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId,
            harness.ConnectionId,
            "T123",
            "U_OWNER",
            "D-first",
            "session-1");

        var stored = await harness.Store.GetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "D-first");

        Assert.Equal("session-1", stored);
    }

    [Fact]
    public async Task SetCurrentSessionIdAsync_NewTask_OverwritesExistingMapping()
    {
        await using var harness = CreateHarness();
        await harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId,
            harness.ConnectionId,
            "T123",
            "U_OWNER",
            "D-first",
            "session-1");
        await harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId,
            harness.ConnectionId,
            "T123",
            "U_OWNER",
            "D-first",
            "session-2");

        var stored = await harness.Store.GetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "D-first");
        Assert.Equal("session-2", stored);

        await using var db = harness.Factory.CreateDbContext();
        var rows = await db.SlackDmSessionMappings
            .Where(row => row.ConnectionId == harness.ConnectionId && row.DmConversationId == "D-first")
            .ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task SetCurrentSessionIdAsync_IsolatesConversationsOnSameConnection()
    {
        await using var harness = CreateHarness();
        await harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "T123", "U_OWNER", "D-A", "session-A");
        await harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "T123", "U_OWNER", "D-B", "session-B");

        Assert.Equal("session-A",
            await harness.Store.GetCurrentSessionIdAsync(harness.ProjectId, harness.ConnectionId, "D-A"));
        Assert.Equal("session-B",
            await harness.Store.GetCurrentSessionIdAsync(harness.ProjectId, harness.ConnectionId, "D-B"));
    }

    [Fact]
    public async Task SetCurrentSessionIdAsync_ThrowsOnEmptyArguments()
    {
        await using var harness = CreateHarness();
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.SetCurrentSessionIdAsync(
            string.Empty, harness.ConnectionId, "T123", "U_OWNER", "D-x", "session-1"));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId, string.Empty, "T123", "U_OWNER", "D-x", "session-1"));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "T123", "U_OWNER", string.Empty, "session-1"));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "T123", "U_OWNER", "D-x", string.Empty));
    }

    [Fact]
    public async Task GetCurrentSessionIdAsync_IsolatesProjects()
    {
        await using var harness = CreateHarness();
        await harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "T123", "U_OWNER", "D-1", "session-project-1");

        var otherProjectId = $"project_{Guid.NewGuid():N}";
        var stored = await harness.Store.GetCurrentSessionIdAsync(
            otherProjectId, harness.ConnectionId, "D-1");

        Assert.Null(stored);
    }

    [Fact]
    public async Task DeleteForConnectionAsync_RemovesAllMappingsForOneConnection()
    {
        await using var harness = CreateHarness();
        await harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "T123", "U_OWNER", "D-1", "session-1");
        await harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "T123", "U_OWNER", "D-2", "session-2");
        await harness.Store.SetCurrentSessionIdAsync(
            "project_other", "connection_other", "T123", "U_OWNER", "D-3", "session-other");

        var affected = await harness.Store.DeleteForConnectionAsync(
            harness.ProjectId, harness.ConnectionId);

        Assert.Equal(2, affected);
        Assert.Null(await harness.Store.GetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "D-1"));
        Assert.Null(await harness.Store.GetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "D-2"));
        Assert.Equal("session-other",
            await harness.Store.GetCurrentSessionIdAsync("project_other", "connection_other", "D-3"));
    }

    [Fact]
    public async Task DeleteForConnectionAsync_ReturnsZeroWhenNothingToDelete()
    {
        await using var harness = CreateHarness();
        var affected = await harness.Store.DeleteForConnectionAsync(
            harness.ProjectId, "connection_missing");
        Assert.Equal(0, affected);
    }

    [Fact]
    public async Task DeleteForConnectionAsync_ImplementsIAgentConnectionProviderCleanup()
    {
        await using var harness = CreateHarness();
        Assert.IsAssignableFrom<IAgentConnectionProviderCleanup>(harness.Store);
    }

    [Fact]
    public async Task Upsert_AdvancesUpdatedAtFromTimeProvider()
    {
        await using var harness = CreateHarness();
        await harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "T123", "U_OWNER", "D-time", "session-1");

        await using (var db = harness.Factory.CreateDbContext())
        {
            var first = await db.SlackDmSessionMappings.SingleAsync();
            Assert.Equal(FixedNow.UtcDateTime, first.UpdatedAt.UtcDateTime);
        }

        harness.Time.SetUtcNow(NextNow);
        await harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "T123", "U_OWNER", "D-time", "session-2");

        await using (var db = harness.Factory.CreateDbContext())
        {
            var second = await db.SlackDmSessionMappings.SingleAsync();
            Assert.Equal(NextNow.UtcDateTime, second.UpdatedAt.UtcDateTime);
            Assert.Equal("session-2", second.CurrentSessionId);
        }
    }

    private static Harness CreateHarness()
    {
        var keeper = new SqliteConnection($"Data Source=dm-store-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        keeper.Open();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(keeper)
            .Options;
        using (var db = new MohistDbContext(options))
        {
            db.Database.EnsureCreated();
        }
        var factory = new TestDbContextFactory(options);
        var time = new FakeTimeProvider(FixedNow);
        var store = new SlackDmSessionMappingStore(factory, time);
        return new Harness(
            Store: store,
            Factory: factory,
            Time: time,
            ProjectId: $"project_{Guid.NewGuid():N}",
            ConnectionId: $"connection_{Guid.NewGuid():N}",
            Connection: keeper);
    }

    private sealed record Harness(
        SlackDmSessionMappingStore Store,
        IDbContextFactory<MohistDbContext> Factory,
        FakeTimeProvider Time,
        string ProjectId,
        string ConnectionId,
        SqliteConnection Connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}
