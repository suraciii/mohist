using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L0")]
public sealed class SlackStatusProjectionTests
{
    [Fact]
    public async Task Connection_session_card_requires_a_stable_session_id_before_writing_liveness()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        await SeedConnectionAsync(database, time);
        var store = CreateStore(database, time);
        var projection = new SlackStatusProjection(store);

        await Assert.ThrowsAsync<ArgumentNullException>(() => projection.EnqueueWorkingAsync(
            "p1", "c1", new SlackMessageIdentity("T1", "C1", "100.001"), null));

        Assert.Empty((await store.ListAsync("p1", "c1")).Entries);
    }

    [Fact]
    public async Task Accepted_status_projection_keeps_the_session_card_separate_from_a_system_failure()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        await SeedConnectionAsync(database, time);
        var store = CreateStore(database, time);
        var projection = new SlackStatusProjection(store);
        var source = new SlackMessageIdentity("T1", "D1", "100.001");

        await projection.EnqueueReceivedAsync("p1", "c1", source, null);
        await projection.EnqueueReceivedAsync("p1", "c1", source, null);
        await projection.EnqueueWorkingAsync("p1", "c1", source, null, sessionId: "session-1");
        await projection.EnqueueWorkingAsync("p1", "c1", source, null, sessionId: "session-1");
        await projection.EnqueueFailureAsync("p1", "c1", source, null, "failed");

        var rows = (await store.ListAsync("p1", "c1")).Entries;
        var received = Assert.Single(rows, row => row.DispatchRef == SlackStatusProjection.DispatchRef(source, "received"));
        Assert.Equal(SlackOutboxKinds.UserAction, received.Kind);
        Assert.Equal("reaction_add", JsonDocument.Parse(received.PayloadJson).RootElement.GetProperty("operation").GetString());
        Assert.Single(rows, row => row.Kind == SlackOutboxKinds.ReplaceableProgress);
        var failure = Assert.Single(rows, row => row.Kind == SlackOutboxKinds.ExplicitFailure);
        Assert.Equal(SlackDeliveryOperations.PostMessage, SlackDeliveryPayload.Parse(failure.PayloadJson).Operation);
    }

    [Fact]
    public async Task Progress_upsert_preserves_the_confirmed_provider_identity()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        await SeedConnectionAsync(database, time);
        var store = CreateStore(database, time);
        var projection = new SlackStatusProjection(store);
        var source = new SlackMessageIdentity("T1", "C1", "100.001");

        var progress = await projection.EnqueueWorkingAsync(
            "p1", "c1", source, null, sessionId: "agent-session-1");
        await store.MarkDeliveredAsync("p1", progress.Id, new SlackProviderMessageIdentity("C1", "100.002"));
        await projection.EnqueueWorkingAsync(
            "p1", "c1", source, null, sessionId: "agent-session-1");

        var current = Assert.Single(
            (await store.ListAsync("p1", "c1")).Entries,
            entry => entry.Kind == SlackOutboxKinds.ReplaceableProgress);
        Assert.Equal(
            new SlackProviderMessageIdentity("C1", "100.002"),
            SlackDeliveryPayload.Parse(current.PayloadJson).ProviderMessageIdentity);
        var payload = SlackDeliveryPayload.Parse(current.PayloadJson);
        Assert.Equal("Agent session.\nSession: agent-session-1", payload.Text);
        Assert.DoesNotContain("Working", payload.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SlackOutboxStates.Claimed)]
    [InlineData(SlackOutboxStates.Delivered)]
    [InlineData(SlackOutboxStates.DeliveryUncertain)]
    public async Task Replayed_session_card_does_not_revive_a_non_pending_delivery(string state)
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        await SeedConnectionAsync(database, time);
        var store = CreateStore(database, time);
        var projection = new SlackStatusProjection(store);
        var source = new SlackMessageIdentity("T1", "C1", "100.001");

        var card = await projection.EnqueueWorkingAsync(
            "p1", "c1", source, null, sessionId: "agent-session-1");
        await using (var db = database.CreateContext())
        {
            var row = await db.SlackOutboxRows.SingleAsync(candidate => candidate.Id == card.Id);
            row.State = state;
            await db.SaveChangesAsync();
        }

        var replay = await projection.EnqueueWorkingAsync(
            "p1", "c1", source, null, sessionId: "agent-session-2");

        Assert.True(replay.MergedIntoExisting);
        Assert.Equal(card.Id, replay.Id);
        var persisted = Assert.Single(
            (await store.ListAsync("p1", "c1")).Entries,
            entry => entry.Id == card.Id);
        Assert.Equal(state, persisted.State);
        Assert.Equal(
            "Agent session.\nSession: agent-session-1",
            SlackDeliveryPayload.Parse(persisted.PayloadJson).Text);
    }

    [Fact]
    public async Task Reenable_pruning_keeps_current_progress_but_drops_stale_reaction_mutations()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        await SeedConnectionAsync(database, time);
        var store = CreateStore(database, time);
        var projection = new SlackStatusProjection(store);
        var source = new SlackMessageIdentity("T1", "C1", "100.001");

        await projection.EnqueueReceivedAsync("p1", "c1", source, null);
        await projection.EnqueueWorkingAsync("p1", "c1", source, null, sessionId: "session-pruning");
        var deleted = await store.PrunePendingStatusMutationsAsync("p1", "c1");

        Assert.Equal(3, deleted);
        Assert.Single((await store.ListAsync("p1", "c1")).Entries, entry => entry.Kind == SlackOutboxKinds.ReplaceableProgress);
    }

    [Fact]
    public async Task System_failure_does_not_reuse_the_session_card_provider_identity()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        await SeedConnectionAsync(database, time);
        var store = CreateStore(database, time);
        var projection = new SlackStatusProjection(store);
        var source = new SlackMessageIdentity("T1", "C1", "100.001");

        var progress = await projection.EnqueueWorkingAsync(
            "p1", "c1", source, "100.000", sessionId: "session-failure");
        await store.MarkDeliveredAsync("p1", progress.Id, new SlackProviderMessageIdentity("C1", "100.002"));
        var blocks = JsonSerializer.SerializeToElement(new[]
        {
            new { type = "actions", elements = new[] { new { type = "button", url = "https://mohist.example/p1/sessions/s1" } }, },
        });
        await projection.EnqueueFailureAsync("p1", "c1", source, "100.000", "failed", blocks: blocks);

        var rows = (await store.ListAsync("p1", "c1")).Entries;
        var card = Assert.Single(rows, row => row.Id == progress.Id);
        Assert.Equal(SlackOutboxKinds.ReplaceableProgress, card.Kind);
        Assert.Equal(SlackOutboxStates.Delivered, card.State);
        var terminal = Assert.Single(rows, row => row.Kind == SlackOutboxKinds.ExplicitFailure);
        var payload = SlackDeliveryPayload.Parse(terminal.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.PostMessage, payload.Operation);
        Assert.Null(payload.ProviderMessageIdentity);
        Assert.Null(payload.StatusDispatchRef);
        Assert.True(payload.Blocks.HasValue);
        Assert.Equal(blocks.GetRawText(), payload.Blocks.Value.GetRawText());
    }

    [Fact]
    public async Task System_failure_does_not_update_the_original_message_from_a_reaction_ack()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        await SeedConnectionAsync(database, time);
        var store = CreateStore(database, time);
        var projection = new SlackStatusProjection(store);
        var source = new SlackMessageIdentity("T1", "C1", "100.001");

        await projection.EnqueueReceivedAsync("p1", "c1", source, null);
        var received = Assert.Single(
            (await store.ListAsync("p1", "c1")).Entries,
            entry => entry.DispatchRef == SlackStatusProjection.DispatchRef(source, "received"));
        await store.MarkDeliveredAsync("p1", received.Id, new SlackProviderMessageIdentity("C1", "100.001"));

        await projection.EnqueueFailureAsync("p1", "c1", source, null, "failed");

        var terminal = Assert.Single(
            (await store.ListAsync("p1", "c1")).Entries,
            row => row.Kind == SlackOutboxKinds.ExplicitFailure);
        var payload = SlackDeliveryPayload.Parse(terminal.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.PostMessage, payload.Operation);
        Assert.Null(payload.ProviderMessageIdentity);
    }

    [Fact]
    public async Task Accepted_terminal_delivery_is_retained_while_disabled_and_claimed_after_reenable()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        await SeedConnectionAsync(database, time, DesiredStateKind.Disabled);
        var store = CreateStore(database, time);
        var projection = new SlackStatusProjection(store);
        var source = new SlackMessageIdentity("T1", "C1", "100.001");

        var result = await projection.EnqueueFailureAsync("p1", "c1", source, null, "failed");

        Assert.False(result.Suppressed);
        Assert.Single(
            (await store.ListAsync("p1", "c1")).Entries,
            entry => entry.Id == result.Id
                && entry.Kind == SlackOutboxKinds.ExplicitFailure
                && entry.State == SlackOutboxStates.Pending);
        Assert.Null(await store.ClaimAsync("p1", "c1", "adapter-1"));

        await using (var db = database.CreateContext())
        {
            var connection = await db.AgentConnections.SingleAsync(row => row.Id == "c1");
            connection.DesiredState = DesiredStateKind.Enabled;
            await db.SaveChangesAsync();
        }

        Assert.Equal(2, await store.PrunePendingStatusMutationsAsync("p1", "c1"));
        var claimed = await store.ClaimAsync("p1", "c1", "adapter-1");
        Assert.Equal(result.Id, claimed?.Id);
        Assert.Equal(SlackOutboxKinds.ExplicitFailure, claimed?.Kind);
    }

    [Fact]
    public async Task Deleted_connection_suppresses_new_status_delivery()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        await SeedConnectionAsync(database, time);
        await using (var db = database.CreateContext())
        {
            var connection = await db.AgentConnections.SingleAsync(row => row.Id == "c1");
            connection.DeletedAt = time.GetUtcNow();
            await db.SaveChangesAsync();
        }

        var store = CreateStore(database, time);
        var projection = new SlackStatusProjection(store);
        var result = await projection.EnqueueFailureAsync(
            "p1", "c1", new SlackMessageIdentity("T1", "C1", "100.001"), null, "failed");

        Assert.True(result.Suppressed);
        Assert.Empty((await store.ListAsync("p1", "c1")).Entries);
    }

    private static SlackOutboxStore CreateStore(TestSqliteDatabase database, FakeTimeProvider time) =>
        new(
            new TestDbContextFactory(database.Options),
            new NoopHealthBackpressurer(),
            time,
            Options.Create(new SlackProviderOptions { OutboxCapacityPerConnection = 20 }));

    private static async Task SeedConnectionAsync(
        TestSqliteDatabase database,
        FakeTimeProvider time,
        string desiredState = DesiredStateKind.Enabled)
    {
        await using var db = database.CreateContext();
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = "c1",
            ProjectId = "p1",
            AgentId = "a1",
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = "T1",
            AppId = "app",
            BotUserId = "bot",
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = desiredState,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            CreatedAt = time.GetUtcNow(),
            UpdatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }

    private sealed class NoopHealthBackpressurer : ISlackConnectionHealthBackpressurer
    {
        public Task FlipBackpressuredAsync(string projectId, string connectionId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> RecoverBackpressuredAsync(string projectId, string connectionId, CancellationToken ct = default) => Task.FromResult(0);
    }
}
