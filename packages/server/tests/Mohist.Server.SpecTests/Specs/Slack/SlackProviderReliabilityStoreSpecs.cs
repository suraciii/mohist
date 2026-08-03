using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackProviderReliabilityStoreSpecs
{
    private static readonly DateTimeOffset Start = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Inbox_DuplicateIdentityReturnsExistingRowEvenWhenAtCapacity()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var health = new RecordingHealthBackpressurer();
        var store = new SlackProviderInboxStore(
            new TestDbContextFactory(database.Options),
            new FakeTimeProvider(Start),
            Options.Create(new SlackProviderOptions { InboxCapacityPerConnection = 1 }),
            health);
        var draft = Draft("1.0000");

        var first = await store.AcceptAsync(draft, Route());
        var duplicate = await store.AcceptAsync(draft, Route());

        Assert.False(first.AlreadyExisted);
        Assert.True(duplicate.AlreadyExisted);
        Assert.Equal(first.Id, duplicate.Id);
        Assert.Empty(health.Reasons);
        Assert.Single((await store.ListAsync("proj_a", "conn_1")).Entries);
    }

    [Fact]
    public async Task Inbox_AcceptPersistsTheRouteBeforeTheMessageCanBeRedelivered()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var health = new RecordingHealthBackpressurer();
        var firstStore = new SlackProviderInboxStore(
            new TestDbContextFactory(database.Options),
            new FakeTimeProvider(Start),
            Options.Create(new SlackProviderOptions { InboxCapacityPerConnection = 1 }),
            health);
        var accepted = await firstStore.AcceptAsync(Draft("3.0000"), Route());
        var restartedStore = new SlackProviderInboxStore(
            new TestDbContextFactory(database.Options),
            new FakeTimeProvider(Start),
            Options.Create(new SlackProviderOptions { InboxCapacityPerConnection = 1 }),
            health);

        var route = await restartedStore.GetRouteAsync("proj_a", accepted.Id);

        Assert.Equal(SlackProviderInboxRouteKinds.Followup, route.Kind);
        Assert.Equal("session-1", route.SessionId);
        Assert.Null(route.TurnId);
    }

    [Fact]
    public async Task Inbox_OverflowRefusesWithoutEvictingAndBackpressuresConnection()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var health = new RecordingHealthBackpressurer();
        var store = new SlackProviderInboxStore(
            new TestDbContextFactory(database.Options),
            new FakeTimeProvider(Start),
            Options.Create(new SlackProviderOptions { InboxCapacityPerConnection = 1 }),
            health);

        await store.AcceptAsync(Draft("1.0000"), Route());
        await Assert.ThrowsAsync<SlackProviderInboxCapacityExceededException>(() => store.AcceptAsync(Draft("2.0000"), Route()));

        Assert.Single((await store.ListAsync("proj_a", "conn_1")).Entries);
        Assert.Equal(SlackProviderBackpressureReasons.InboxOverflow, Assert.Single(health.Reasons));
    }

    [Fact]
    public async Task Outbox_MergesPendingProgressButKeepsTerminalRowsSeparate()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var health = new RecordingHealthBackpressurer();
        var store = NewOutbox(database, health, capacity: 3);

        var first = await store.EnqueueAsync(OutboxDraft(SlackOutboxKinds.ReplaceableProgress, "queued"));
        var merged = await store.EnqueueAsync(OutboxDraft(SlackOutboxKinds.ReplaceableProgress, "executing"));
        var terminal = await store.EnqueueAsync(OutboxDraft(SlackOutboxKinds.TerminalResult, "done"));

        Assert.True(merged.MergedIntoExisting);
        Assert.Equal(first.Id, merged.Id);
        var rows = (await store.ListAsync("proj_a", "conn_1")).Entries;
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Id == first.Id && row.PayloadJson == "\"executing\"");
        Assert.Contains(rows, row => row.Id == terminal.Id && row.Kind == SlackOutboxKinds.TerminalResult);
    }

    [Fact]
    public async Task Outbox_OverflowBackpressuresAndDoesNotDropAcceptedRows()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var health = new RecordingHealthBackpressurer();
        var store = NewOutbox(database, health, capacity: 1);

        await store.EnqueueAsync(OutboxDraft(SlackOutboxKinds.TerminalResult, "done"));
        await Assert.ThrowsAsync<SlackOutboxCapacityExceededException>(() =>
            store.EnqueueAsync(OutboxDraft(SlackOutboxKinds.ExplicitFailure, "failed")));

        Assert.Single((await store.ListAsync("proj_a", "conn_1")).Entries);
        Assert.Equal(SlackProviderBackpressureReasons.OutboxOverflow, Assert.Single(health.Reasons));
    }

    [Fact]
    public async Task Outbox_RequiredAcknowledgementIsIdempotentAcrossStoreRecreation()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var health = new RecordingHealthBackpressurer();
        await SeedEnabledConnectionAsync(database);
        var firstStore = NewOutbox(database, health, capacity: 1);

        var first = await firstStore.EnqueueRequiredAsync(OutboxDraft(SlackOutboxKinds.UserAction, "accepted"));
        var restartedStore = NewOutbox(database, health, capacity: 1);
        var redelivery = await restartedStore.EnqueueRequiredAsync(OutboxDraft(SlackOutboxKinds.UserAction, "accepted"));

        Assert.Equal(first.Id, redelivery.Id);
        Assert.True(redelivery.MergedIntoExisting);
        Assert.Single((await restartedStore.ListAsync("proj_a", "conn_1")).Entries);
    }

    [Fact]
    public async Task Outbox_RequiredAcknowledgementPersistsWhenCapacityIsAlreadyFull()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var health = new RecordingHealthBackpressurer();
        await SeedEnabledConnectionAsync(database);
        var store = NewOutbox(database, health, capacity: 1);

        await store.EnqueueAsync(OutboxDraft(SlackOutboxKinds.TerminalResult, "prior"));
        await store.EnqueueRequiredAsync(OutboxDraft(SlackOutboxKinds.UserAction, "accepted"));

        Assert.Equal(2, (await store.ListAsync("proj_a", "conn_1")).Entries.Count);
        Assert.Equal(SlackProviderBackpressureReasons.OutboxOverflow, Assert.Single(health.Reasons));
    }

    [Fact]
    public async Task Outbox_ClaimDoesNotClaimBeforeRetryTimeAndUncertaintyIsTerminal()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var store = NewOutbox(database, new RecordingHealthBackpressurer(), capacity: 3, time);
        var queued = await store.EnqueueAsync(OutboxDraft(SlackOutboxKinds.TerminalResult, "done"));

        await store.ScheduleRetryAsync("proj_a", queued.Id, "transient");
        Assert.Null(await store.ClaimAsync("proj_a", "conn_1", "adapter-a"));
        time.Advance(TimeSpan.FromSeconds(1));
        await store.ClaimAsync("proj_a", "conn_1", "adapter-a");
        await store.MarkDeliveryUncertainAsync("proj_a", queued.Id, "no Slack acknowledgement");

        var uncertain = Assert.Single((await store.ListAsync("proj_a", "conn_1")).Entries);
        Assert.Equal(SlackOutboxStates.DeliveryUncertain, uncertain.State);
        await Assert.ThrowsAsync<SlackOutboxStateException>(() => store.MarkDeliveredAsync("proj_a", queued.Id));
    }

    [Fact]
    public async Task Outbox_ack_rejects_a_stale_adapter_lease()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        await SeedEnabledConnectionAsync(database);
        var store = NewOutbox(database, new RecordingHealthBackpressurer(), capacity: 3);
        var queued = await store.EnqueueAsync(OutboxDraft(SlackOutboxKinds.TerminalResult, "done"));
        var claimed = await store.ClaimAsync("proj_a", "conn_1", "adapter-a");

        Assert.NotNull(claimed);
        await Assert.ThrowsAsync<SlackOutboxStateException>(() =>
            store.MarkDeliveredAsync("proj_a", queued.Id, null, "adapter-b"));

        await store.MarkDeliveredAsync("proj_a", queued.Id, null, "adapter-a");
        Assert.Equal(
            SlackOutboxStates.Delivered,
            Assert.Single((await store.ListAsync("proj_a", "conn_1")).Entries).State);
    }

    private static SlackProviderInboxDraft Draft(string messageTs) => new(
        "proj_a", "conn_1", new SlackMessageIdentity("team-1", "D1", messageTs), "U1");

    private static SlackProviderInboxRouteDraft Route() =>
        new(SlackProviderInboxRouteKinds.Followup, "session-1");

    private static SlackOutboxDraft OutboxDraft(string kind, string payload) => new(
        "proj_a", "conn_1", "team-1", "D1", kind, "dispatch-1", $"\"{payload}\"");

    private static SlackOutboxStore NewOutbox(
        TestSqliteDatabase database,
        RecordingHealthBackpressurer health,
        int capacity,
        FakeTimeProvider? time = null) => new(
            new TestDbContextFactory(database.Options),
            health,
            time ?? new FakeTimeProvider(Start),
            Options.Create(new SlackProviderOptions { OutboxCapacityPerConnection = capacity }));

    private static async Task SeedEnabledConnectionAsync(TestSqliteDatabase database)
    {
        await using var db = database.CreateContext();
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = "conn_1",
            ProjectId = "proj_a",
            AgentId = "agent_1",
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = "team-1",
            AppId = "app-1",
            BotUserId = "bot-1",
            BotName = "Mohist",
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            CreatedAt = Start,
            UpdatedAt = Start,
        });
        await db.SaveChangesAsync();
    }

    private sealed class RecordingHealthBackpressurer : ISlackConnectionHealthBackpressurer
    {
        public List<string> Reasons { get; } = [];
        public List<(string ProjectId, string ConnectionId)> Recoveries { get; } = [];

        public Task FlipBackpressuredAsync(string projectId, string connectionId, string reason, CancellationToken ct = default)
        {
            Reasons.Add(reason);
            return Task.CompletedTask;
        }

        public Task<int> RecoverBackpressuredAsync(string projectId, string connectionId, CancellationToken ct = default)
        {
            Recoveries.Add((projectId, connectionId));
            return Task.FromResult(1);
        }
    }
}
