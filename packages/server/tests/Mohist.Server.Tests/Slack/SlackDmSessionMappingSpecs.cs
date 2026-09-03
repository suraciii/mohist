using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Tests.Support;
using Mohist.Server.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L0")]
public sealed class SlackDmSessionMappingSpecs
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

    [Fact]
    public async Task SetCurrentSessionIdAsync_DoesNotLetAnOlderMessageReplaceTheCurrentSession()
    {
        await using var harness = CreateHarness();
        await harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "T123", "U_OWNER", "D-order", "session-new", "1710000000.000200");
        await harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "T123", "U_OWNER", "D-order", "session-old", "1710000000.000100");

        Assert.Equal("session-new", await harness.Store.GetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "D-order"));
    }

    [Fact]
    public async Task SetCurrentSessionIdAsync_ConcurrentMessagesKeepTheLatestMessage()
    {
        await using var harness = CreateHarness();

        await Task.WhenAll(
            harness.Store.SetCurrentSessionIdAsync(
                harness.ProjectId, harness.ConnectionId, "T123", "U_OWNER", "D-order", "session-old", "1710000000.000100"),
            harness.Store.SetCurrentSessionIdAsync(
                harness.ProjectId, harness.ConnectionId, "T123", "U_OWNER", "D-order", "session-new", "1710000000.000200"));

        Assert.Equal("session-new", await harness.Store.GetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "D-order"));
    }

    [Fact]
    public async Task Dm_route_draft_selects_new_task_launch_first_mapping_or_followup_without_http()
    {
        await using var harness = CreateHarness();
        await harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId,
            harness.ConnectionId,
            "T123",
            "U_OWNER",
            "D-route",
            "session-current");

        var first = await SlackConnectionRoutes.ResolveInboxRouteDraftAsync(
            harness.ProjectId,
            harness.ConnectionId,
            "D-first",
            isNewTask: false,
            harness.Store,
            CancellationToken.None);
        var followup = await SlackConnectionRoutes.ResolveInboxRouteDraftAsync(
            harness.ProjectId,
            harness.ConnectionId,
            "D-route",
            isNewTask: false,
            harness.Store,
            CancellationToken.None);
        var newTask = await SlackConnectionRoutes.ResolveInboxRouteDraftAsync(
            harness.ProjectId,
            harness.ConnectionId,
            "D-route",
            isNewTask: true,
            harness.Store,
            CancellationToken.None);

        Assert.Equal(SlackProviderInboxRouteKinds.Launch, first.Kind);
        Assert.Equal(SlackProviderInboxRouteKinds.Followup, followup.Kind);
        Assert.Equal("session-current", followup.SessionId);
        Assert.False(SlackDmIngressPolicy.RequiresNewWorkAdmission(false, followup.SessionId));
        Assert.Equal(SlackProviderInboxRouteKinds.NewTaskLaunch, newTask.Kind);
        Assert.Null(newTask.SessionId);
        Assert.True(SlackDmIngressPolicy.RequiresNewWorkAdmission(true, followup.SessionId));
        Assert.True(SlackDmIngressPolicy.RequiresNewWorkAdmission(false, null));
    }

    [Fact]
    public void Recovery_reply_provenance_keeps_the_followup_message_and_bound_thread_root()
    {
        var provenance = new AgentSessionInputProvenance(
            "slack",
            "T123",
            "D-recovery",
            "legacy-thread-root",
            "U_OWNER",
            "1710000000.000200",
            "connection-1",
            "1710000000.000125");

        var origin = AgentSessionRetryService.BuildConnectionLaunchOrigin(provenance);

        Assert.Equal("1710000000.000200", origin.MessageTs);
        Assert.Equal("1710000000.000125", origin.ThreadTs);
    }

    [Fact]
    public async Task ReplaceCurrentSessionAndInboxRouteAsync_atomically_moves_retry_followup_to_replacement()
    {
        await using var harness = CreateHarness();
        const string inboxId = "inbox-1";
        await harness.Store.SetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "T123", "U_OWNER", "D-recovery", "failed-session", "1710000000.000100");

        await using (var db = harness.Factory.CreateDbContext())
        {
            db.SlackProviderInboxRows.Add(new SlackProviderInboxRow
            {
                Id = inboxId,
                ProjectId = harness.ProjectId,
                ConnectionId = harness.ConnectionId,
                SlackMessageIdentity = "T123/D-recovery/1710000000.000200",
                WorkspaceTeamId = "T123",
                ConversationId = "D-recovery",
                SlackUserId = "U_OWNER",
                RouteKind = SlackProviderInboxRouteKinds.Followup,
                RouteSessionId = "failed-session",
                AcceptedAt = FixedNow,
                CreatedAt = FixedNow,
            });
            await db.SaveChangesAsync();
        }

        var replacement = await harness.Store.ReplaceCurrentSessionAndInboxRouteAsync(
            harness.ProjectId,
            harness.ConnectionId,
            "T123",
            "U_OWNER",
            "D-recovery",
            inboxId,
            "failed-session",
            "replacement-session",
            "1710000000.000200");

        Assert.Equal("replacement-session", replacement);
        Assert.Equal("replacement-session", await harness.Store.GetCurrentSessionIdAsync(
            harness.ProjectId, harness.ConnectionId, "D-recovery"));
        await using var verify = harness.Factory.CreateDbContext();
        var route = await verify.SlackProviderInboxRows.SingleAsync(row => row.Id == inboxId);
        Assert.Equal("replacement-session", route.RouteSessionId);
    }

    private static Harness CreateHarness()
    {
        var keeper = new SqliteConnection($"Data Source=dm-store-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        keeper.Open();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(keeper)
            .Options;
        MigratedSqliteTemplate.CopyModelSchemaTo(keeper);
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
