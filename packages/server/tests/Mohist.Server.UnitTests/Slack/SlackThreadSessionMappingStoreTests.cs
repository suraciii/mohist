using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public class SlackThreadSessionMappingStoreTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetSessionIdAsync_ReturnsNullWhenNoMappingExists()
    {
        await using var harness = CreateHarness();
        var sessionId = await harness.Store.GetSessionIdAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-channel", "1710.0001");
        Assert.Null(sessionId);
    }

    [Fact]
    public async Task UpsertAsync_ThenGet_RoundTripsValue()
    {
        await using var harness = CreateHarness();
        await harness.Store.UpsertAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-channel", "1710.0001",
            "U_OWNER", "session-A", "1710.0001");

        var stored = await harness.Store.GetSessionIdAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-channel", "1710.0001");
        Assert.Equal("session-A", stored);
    }

    [Fact]
    public async Task UpsertAsync_IsIdempotent_AndKeepsTheFirstSessionId()
    {
        await using var harness = CreateHarness();
        var first = await harness.Store.UpsertAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-channel", "1710.0001",
            "U_OWNER", "session-A", "1710.0001");
        Assert.False(first.AlreadyExisted);

        var second = await harness.Store.UpsertAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-channel", "1710.0001",
            "U_OWNER", "session-B", "1710.0001");
        Assert.True(second.AlreadyExisted);
        Assert.Equal("session-A", second.SessionId);

        var stored = await harness.Store.GetSessionIdAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-channel", "1710.0001");
        Assert.Equal("session-A", stored);
    }

    [Fact]
    public async Task GetSessionIdAsync_DistinguishesWorkspacesWithEqualThreadTs()
    {
        await using var harness = CreateHarness();
        await harness.Store.UpsertAsync(
            harness.ProjectId, "T-A", harness.ConnectionId, "C-shared", "1710.0001",
            "U_OWNER", "session-A", "1710.0001");
        await harness.Store.UpsertAsync(
            harness.ProjectId, "T-B", harness.ConnectionId, "C-shared", "1710.0001",
            "U_OWNER", "session-B", "1710.0001");

        Assert.Equal("session-A", await harness.Store.GetSessionIdAsync(
            harness.ProjectId, "T-A", harness.ConnectionId, "C-shared", "1710.0001"));
        Assert.Equal("session-B", await harness.Store.GetSessionIdAsync(
            harness.ProjectId, "T-B", harness.ConnectionId, "C-shared", "1710.0001"));
    }

    [Fact]
    public async Task GetSessionIdAsync_DistinguishesChannelsWithEqualThreadTs()
    {
        await using var harness = CreateHarness();
        await harness.Store.UpsertAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-one", "1710.0001",
            "U_OWNER", "session-one", "1710.0001");
        await harness.Store.UpsertAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-two", "1710.0001",
            "U_OWNER", "session-two", "1710.0001");

        Assert.Equal("session-one", await harness.Store.GetSessionIdAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-one", "1710.0001"));
        Assert.Equal("session-two", await harness.Store.GetSessionIdAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-two", "1710.0001"));
    }

    [Fact]
    public async Task ListBindingsAsync_ScopesByWorkspaceAndReturnsAllConnections()
    {
        await using var harness = CreateHarness();
        var secondConnection = $"connection_{Guid.NewGuid():N}";
        await harness.Store.UpsertAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-shared", "1710.0001",
            "U_OWNER", "session-self", "1710.0001");
        await harness.Store.UpsertAsync(
            harness.ProjectId, "T123", secondConnection, "C-shared", "1710.0001",
            "U_OWNER", "session-other", "1710.0001");
        await harness.Store.UpsertAsync(
            harness.ProjectId, "T-OTHER", harness.ConnectionId, "C-shared", "1710.0001",
            "U_OWNER", "session-other-workspace", "1710.0001");

        var bindings = await harness.Store.ListBindingsAsync(
            harness.ProjectId, "T123", "C-shared", "1710.0001");

        Assert.Equal(2, bindings.Count);
        Assert.Contains(bindings, b => b.ConnectionId == harness.ConnectionId && b.SessionId == "session-self");
        Assert.Contains(bindings, b => b.ConnectionId == secondConnection && b.SessionId == "session-other");
    }

    [Fact]
    public async Task ListBindingsAsync_DoesNotLeakAcrossWorkspaces()
    {
        await using var harness = CreateHarness();
        await harness.Store.UpsertAsync(
            harness.ProjectId, "T-A", harness.ConnectionId, "C-shared", "1710.0001",
            "U_OWNER", "session-A", "1710.0001");
        await harness.Store.UpsertAsync(
            harness.ProjectId, "T-B", harness.ConnectionId, "C-shared", "1710.0001",
            "U_OWNER", "session-B", "1710.0001");

        var fromA = await harness.Store.ListBindingsAsync(
            harness.ProjectId, "T-A", "C-shared", "1710.0001");
        var fromB = await harness.Store.ListBindingsAsync(
            harness.ProjectId, "T-B", "C-shared", "1710.0001");

        Assert.Single(fromA);
        Assert.Equal("session-A", fromA[0].SessionId);
        Assert.Single(fromB);
        Assert.Equal("session-B", fromB[0].SessionId);
    }

    [Fact]
    public async Task ListBindingsByWorkspaceAsync_IncludesBindingsAcrossProjects()
    {
        await using var harness = CreateHarness();
        var otherProject = $"project_{Guid.NewGuid():N}";
        var otherConnection = $"connection_{Guid.NewGuid():N}";
        await harness.Store.UpsertAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-shared", "1710.0001",
            "U_OWNER", "session-A", "1710.0001");
        await harness.Store.UpsertAsync(
            otherProject, "T123", otherConnection, "C-shared", "1710.0001",
            "U_OWNER", "session-B", "1710.0001");

        var bindings = await harness.Store.ListBindingsByWorkspaceAsync(
            "T123", "C-shared", "1710.0001");

        Assert.Equal(2, bindings.Count);
        Assert.Contains(bindings, binding => binding.SessionId == "session-A");
        Assert.Contains(bindings, binding => binding.SessionId == "session-B");
    }

    [Fact]
    public async Task UpsertAsync_ThrowsOnEmptyArguments()
    {
        await using var harness = CreateHarness();
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.UpsertAsync(
            string.Empty, "T123", harness.ConnectionId, "C-channel", "1710.0001",
            "U_OWNER", "session-A", "1710.0001"));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.UpsertAsync(
            harness.ProjectId, "T123", string.Empty, "C-channel", "1710.0001",
            "U_OWNER", "session-A", "1710.0001"));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.UpsertAsync(
            harness.ProjectId, "T123", harness.ConnectionId, string.Empty, "1710.0001",
            "U_OWNER", "session-A", "1710.0001"));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.UpsertAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-channel", string.Empty,
            "U_OWNER", "session-A", "1710.0001"));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.UpsertAsync(
            harness.ProjectId, string.Empty, harness.ConnectionId, "C-channel", "1710.0001",
            "U_OWNER", "session-A", "1710.0001"));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.UpsertAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-channel", "1710.0001",
            "U_OWNER", string.Empty, "1710.0001"));
    }

    [Fact]
    public async Task DeleteForConnectionAsync_RemovesAllThreadMappingsForOneConnection()
    {
        await using var harness = CreateHarness();
        var otherConnection = $"connection_{Guid.NewGuid():N}";
        await harness.Store.UpsertAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-one", "1710.0001",
            "U_OWNER", "session-1", "1710.0001");
        await harness.Store.UpsertAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-two", "1710.0001",
            "U_OWNER", "session-2", "1710.0001");
        await harness.Store.UpsertAsync(
            harness.ProjectId, "T123", otherConnection, "C-one", "1710.0001",
            "U_OWNER", "session-other", "1710.0001");

        var affected = await harness.Store.DeleteForConnectionAsync(
            harness.ProjectId, harness.ConnectionId);

        Assert.Equal(2, affected);
        Assert.Null(await harness.Store.GetSessionIdAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-one", "1710.0001"));
        Assert.Null(await harness.Store.GetSessionIdAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-two", "1710.0001"));
        Assert.Equal("session-other", await harness.Store.GetSessionIdAsync(
            harness.ProjectId, "T123", otherConnection, "C-one", "1710.0001"));
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
    public async Task UniqueIndex_PreventsDuplicateBindsUnderConcurrency()
    {
        await using var harness = CreateHarness();
        await harness.Store.UpsertAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-channel", "1710.0001",
            "U_OWNER", "session-A", "1710.0001");

        await using var db = harness.Factory.CreateDbContext();
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            db.SlackThreadSessionMappings.Add(new SlackThreadSessionMappingRow
            {
                Id = "slkthrdsmp_other",
                ProjectId = harness.ProjectId,
                ConnectionId = harness.ConnectionId,
                WorkspaceTeamId = "T123",
                ConversationId = "C-channel",
                ThreadTs = "1710.0001",
                SlackUserId = "U_OWNER",
                SessionId = "session-other",
                RootMessageTs = "1710.0001",
                CreatedAt = FixedNow.UtcDateTime,
                UpdatedAt = FixedNow.UtcDateTime,
            });
            await db.SaveChangesAsync();
        });
    }

    private static Harness CreateHarness()
    {
        var keeper = new SqliteConnection($"Data Source=thread-store-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        keeper.Open();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(keeper)
            .Options;
        SqliteSchemaTemplate.CopyModelSchemaTo(keeper);
        var factory = new TestDbContextFactory(options);
        var time = new FakeTimeProvider(FixedNow);
        var store = new SlackThreadSessionMappingStore(factory, time);
        return new Harness(
            Store: store,
            Factory: factory,
            Time: time,
            ProjectId: $"project_{Guid.NewGuid():N}",
            ConnectionId: $"connection_{Guid.NewGuid():N}",
            Connection: keeper);
    }

    private sealed record Harness(
        SlackThreadSessionMappingStore Store,
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
