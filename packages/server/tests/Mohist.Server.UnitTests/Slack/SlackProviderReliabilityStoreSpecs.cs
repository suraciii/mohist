using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackProviderReliabilityStoreSpecs
{
    private static readonly DateTimeOffset Start = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Inbox_DuplicateIdentityReturnsExistingRowEvenWhenAtCapacity()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
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
        using var database = TestSqliteDatabase.CreateModelSchema();
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
        using var database = TestSqliteDatabase.CreateModelSchema();
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
        using var database = TestSqliteDatabase.CreateModelSchema();
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
        using var database = TestSqliteDatabase.CreateModelSchema();
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
        using var database = TestSqliteDatabase.CreateModelSchema();
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
        using var database = TestSqliteDatabase.CreateModelSchema();
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
        using var database = TestSqliteDatabase.CreateModelSchema();
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
    public async Task Outbox_uncertain_reconciliation_and_retry_keep_one_row_and_stable_delivery_identity()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        await SeedEnabledConnectionAsync(database);
        var time = new FakeTimeProvider(Start);
        var store = NewOutbox(database, new RecordingHealthBackpressurer(), capacity: 3, time);
        var dispatchRef = "slack-admission-nudge:stable";
        var payload = System.Text.Json.JsonSerializer.Serialize(new SlackDeliveryPayload(
            SlackDeliveryOperations.PostMessage,
            "The Agent is not ready.",
            ClientMessageId: dispatchRef));
        var queued = await store.EnqueueRequiredAsync(new SlackOutboxDraft(
            "proj_a", "conn_1", "team-1", "D1", SlackOutboxKinds.UserAction,
            dispatchRef, payload, "1710000000.000001"));

        var claimed = await store.ClaimAsync("proj_a", "conn_1", "adapter-a");
        Assert.Equal(queued.Id, claimed?.Id);
        await store.MarkDeliveryUncertainAsync("proj_a", queued.Id, "provider response lost", "adapter-a");
        var uncertain = Assert.Single((await store.ListAsync("proj_a", "conn_1")).Entries);
        Assert.Equal(queued.Id, uncertain.Id);
        Assert.Equal(dispatchRef, uncertain.DispatchRef);
        Assert.Equal(dispatchRef, SlackDeliveryPayload.Parse(uncertain.PayloadJson).ClientMessageId);

        await store.ClaimUncertainAsync("proj_a", "conn_1", "adapter-b");
        await store.ScheduleRetryAsync("proj_a", queued.Id, "provider message absent", "adapter-b");
        var retry = Assert.Single((await store.ListAsync("proj_a", "conn_1")).Entries);
        Assert.Equal(queued.Id, retry.Id);
        Assert.Equal(dispatchRef, retry.DispatchRef);
        Assert.Equal(dispatchRef, SlackDeliveryPayload.Parse(retry.PayloadJson).ClientMessageId);

        time.Advance(TimeSpan.FromMinutes(1));
        await store.ClaimAsync("proj_a", "conn_1", "adapter-c");
        await store.MarkDeliveredAsync(
            "proj_a",
            queued.Id,
            new SlackProviderMessageIdentity("D1", "1710000000.000002"),
            "adapter-c");
        var delivered = Assert.Single((await store.ListAsync("proj_a", "conn_1")).Entries);
        Assert.Equal(queued.Id, delivered.Id);
        Assert.Equal(SlackOutboxStates.Delivered, delivered.State);
        Assert.Equal(dispatchRef, delivered.DispatchRef);
        var deliveredPayload = SlackDeliveryPayload.Parse(delivered.PayloadJson);
        Assert.Equal(dispatchRef, deliveredPayload.ClientMessageId);
        Assert.Equal("1710000000.000002", deliveredPayload.ProviderMessageIdentity?.MessageTs);
    }

    [Fact]
    public async Task Outbox_ack_rejects_a_stale_adapter_lease()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
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

    [Fact]
    public async Task Outbox_dead_letter_skips_a_row_claimed_after_the_sweep_listing()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        await SeedEnabledConnectionAsync(database);
        var store = NewOutbox(database, new RecordingHealthBackpressurer(), capacity: 3);
        var queued = await store.EnqueueAsync(OutboxDraft(SlackOutboxKinds.TerminalResult, "done"));
        Assert.NotNull(await store.ClaimAsync("proj_a", "conn_1", "adapter-a"));

        var updated = await store.MarkDeadLetteredAsync(
            "proj_a",
            queued.Id,
            "retry budget exhausted",
            expectedState: SlackOutboxStates.Pending,
            expectedUpdatedAt: Start);

        Assert.Equal(0, updated);
        Assert.Equal(
            SlackOutboxStates.Claimed,
            Assert.Single((await store.ListAsync("proj_a", "conn_1")).Entries).State);
    }

    [Fact]
    public async Task Outbox_dead_letter_fence_preserves_a_retry_ack_after_the_sweep_listing()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        await SeedEnabledConnectionAsync(database);
        var time = new FakeTimeProvider(Start);
        var store = NewOutbox(database, new RecordingHealthBackpressurer(), capacity: 3, time, maxAttempts: 1);
        var queued = await store.EnqueueAsync(OutboxDraft(SlackOutboxKinds.TerminalResult, "done"));

        await using (var db = database.CreateContext())
        {
            var row = await db.SlackOutboxRows.SingleAsync(candidate => candidate.Id == queued.Id);
            row.AttemptCount = 1;
            await db.SaveChangesAsync();
        }

        var listedRows = await store.ListPendingReadyForRetryAsync(1);
        var listed = Assert.Single(listedRows);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(await store.ClaimAsync("proj_a", "conn_1", "adapter-a"));
        time.Advance(TimeSpan.FromSeconds(1));
        await store.ScheduleRetryAsync("proj_a", queued.Id, "transient", "adapter-a");

        var updated = await store.MarkDeadLetteredAsync(
            listed.ProjectId,
            listed.Id,
            "retry budget exhausted",
            expectedState: SlackOutboxStates.Pending,
            expectedUpdatedAt: listed.UpdatedAt);

        var rowAfterSweep = Assert.Single((await store.ListAsync("proj_a", "conn_1")).Entries);
        Assert.Equal(0, updated);
        Assert.Equal(SlackOutboxStates.Pending, rowAfterSweep.State);
        Assert.Equal(2, rowAfterSweep.AttemptCount);
        Assert.Equal("transient", rowAfterSweep.LastError);
        Assert.True(rowAfterSweep.UpdatedAt > listed.UpdatedAt);
    }

    [Fact]
    public async Task Outbox_claim_timeout_skips_a_row_delivered_after_the_sweep_listing()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        await SeedEnabledConnectionAsync(database);
        var store = NewOutbox(database, new RecordingHealthBackpressurer(), capacity: 3);
        var queued = await store.EnqueueAsync(OutboxDraft(SlackOutboxKinds.TerminalResult, "done"));
        Assert.NotNull(await store.ClaimAsync("proj_a", "conn_1", "adapter-a"));
        await store.MarkDeliveredAsync("proj_a", queued.Id, null, "adapter-a");

        var updated = await store.MarkDeliveryUncertainAsync(
            "proj_a",
            queued.Id,
            "claim timeout",
            adapterId: null,
            expectedState: SlackOutboxStates.Claimed,
            expectedUpdatedAt: Start);

        Assert.Equal(0, updated);
        Assert.Equal(
            SlackOutboxStates.Delivered,
            Assert.Single((await store.ListAsync("proj_a", "conn_1")).Entries).State);
    }

    [Fact]
    public async Task Outbox_claim_timeout_skips_a_row_reclaimed_after_the_sweep_listing()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        await SeedEnabledConnectionAsync(database);
        var time = new FakeTimeProvider(Start);
        var store = NewOutbox(database, new RecordingHealthBackpressurer(), capacity: 3, time);
        var queued = await store.EnqueueAsync(OutboxDraft(SlackOutboxKinds.TerminalResult, "done"));
        var listed = (await store.ClaimAsync("proj_a", "conn_1", "adapter-a"))!;
        time.Advance(TimeSpan.FromSeconds(1));
        await store.ScheduleRetryAsync("proj_a", queued.Id, "transient", "adapter-a");
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(await store.ClaimAsync("proj_a", "conn_1", "adapter-b"));

        var updated = await store.MarkDeliveryUncertainAsync(
            "proj_a",
            queued.Id,
            "claim timeout",
            adapterId: null,
            expectedState: SlackOutboxStates.Claimed,
            expectedUpdatedAt: listed.UpdatedAt);

        var row = Assert.Single((await store.ListAsync("proj_a", "conn_1")).Entries);
        Assert.Equal(0, updated);
        Assert.Equal(SlackOutboxStates.Claimed, row.State);
        Assert.Equal("adapter-b", row.ClaimedByAdapterId);
        Assert.Equal("transient", row.LastError);
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
        FakeTimeProvider? time = null,
        int maxAttempts = 5) => new(
            new TestDbContextFactory(database.Options),
            health,
            time ?? new FakeTimeProvider(Start),
            Options.Create(new SlackProviderOptions
            {
                OutboxCapacityPerConnection = capacity,
                OutboxMaxAttempts = maxAttempts,
            }));

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
