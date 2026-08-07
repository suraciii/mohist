using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackStatusProjectionSpecs
{
    [Fact]
    public async Task Accepted_status_projection_is_deduplicated_and_pending_progress_is_promoted_to_terminal()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        await SeedConnectionAsync(database, time);
        var store = CreateStore(database, time);
        var projection = new SlackStatusProjection(store);
        var source = new SlackMessageIdentity("T1", "D1", "100.001");

        await projection.EnqueueReceivedAsync("p1", "c1", source, null);
        await projection.EnqueueReceivedAsync("p1", "c1", source, null);
        await projection.EnqueueWorkingAsync("p1", "c1", source, null);
        await projection.EnqueueWorkingAsync("p1", "c1", source, null);
        await projection.EnqueueTerminalAsync("p1", "c1", source, null, "completed", "done");

        var rows = (await store.ListAsync("p1", "c1")).Entries;
        Assert.Equal(6, rows.Count);
        var received = Assert.Single(rows, row => row.DispatchRef == SlackStatusProjection.DispatchRef(source, "received"));
        Assert.Equal(SlackOutboxKinds.UserAction, received.Kind);
        Assert.Equal("reaction_add", JsonDocument.Parse(received.PayloadJson).RootElement.GetProperty("operation").GetString());
        Assert.Equal(SlackOutboxKinds.TerminalResult, Assert.Single(rows, row => row.DispatchRef == SlackStatusProjection.DispatchRef(source, "terminal")).Kind);
        Assert.DoesNotContain(rows, row => row.Kind == SlackOutboxKinds.ReplaceableProgress);
        Assert.Equal(4, rows.Count(row => row.Kind == SlackOutboxKinds.UserAction
            && row.DispatchRef != SlackStatusProjection.DispatchRef(source, "received")));
    }

    [Fact]
    public async Task Progress_upsert_preserves_the_confirmed_provider_identity()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        await SeedConnectionAsync(database, time);
        var store = CreateStore(database, time);
        var projection = new SlackStatusProjection(store);
        var source = new SlackMessageIdentity("T1", "C1", "100.001");

        var progress = await projection.EnqueueWorkingAsync("p1", "c1", source, null);
        await store.MarkDeliveredAsync("p1", progress.Id, new SlackProviderMessageIdentity("C1", "100.002"));
        await projection.EnqueueWorkingAsync("p1", "c1", source, null);

        var current = Assert.Single(
            (await store.ListAsync("p1", "c1")).Entries,
            entry => entry.Kind == SlackOutboxKinds.ReplaceableProgress);
        Assert.Equal(
            new SlackProviderMessageIdentity("C1", "100.002"),
            SlackDeliveryPayload.Parse(current.PayloadJson).ProviderMessageIdentity);
    }

    [Fact]
    public async Task Reenable_pruning_keeps_current_progress_but_drops_stale_reaction_mutations()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        await SeedConnectionAsync(database, time);
        var store = CreateStore(database, time);
        var projection = new SlackStatusProjection(store);
        var source = new SlackMessageIdentity("T1", "C1", "100.001");

        await projection.EnqueueReceivedAsync("p1", "c1", source, null);
        await projection.EnqueueWorkingAsync("p1", "c1", source, null);
        var deleted = await store.PrunePendingStatusMutationsAsync("p1", "c1");

        Assert.Equal(3, deleted);
        Assert.Single((await store.ListAsync("p1", "c1")).Entries, entry => entry.Kind == SlackOutboxKinds.ReplaceableProgress);
    }

    [Fact]
    public async Task Terminal_projection_uses_the_confirmed_progress_provider_identity_for_chat_update()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        await SeedConnectionAsync(database, time);
        var store = CreateStore(database, time);
        var projection = new SlackStatusProjection(store);
        var source = new SlackMessageIdentity("T1", "C1", "100.001");

        var progress = await projection.EnqueueWorkingAsync("p1", "c1", source, "100.000");
        await store.MarkDeliveredAsync("p1", progress.Id, new SlackProviderMessageIdentity("C1", "100.002"));
        var blocks = JsonSerializer.SerializeToElement(new[]
        {
            new { type = "actions", elements = new[] { new { type = "button", url = "https://mohist.example/p1/sessions/s1" } }, },
        });
        await projection.EnqueueTerminalAsync("p1", "c1", source, "100.000", "failed", "failed", blocks: blocks);

        var terminal = Assert.Single((await store.ListAsync("p1", "c1")).Entries, row => row.Kind == SlackOutboxKinds.ExplicitFailure);
        var payload = SlackDeliveryPayload.Parse(terminal.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.ChatUpdate, payload.Operation);
        Assert.Equal(new SlackProviderMessageIdentity("C1", "100.002"), payload.ProviderMessageIdentity);
        Assert.True(payload.Blocks.HasValue);
        Assert.Equal(blocks.GetRawText(), payload.Blocks.Value.GetRawText());
    }

    [Fact]
    public async Task Quick_terminal_does_not_update_the_original_message_from_a_reaction_ack()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
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

        await projection.EnqueueTerminalAsync("p1", "c1", source, null, "completed", "done");

        var terminal = Assert.Single(
            (await store.ListAsync("p1", "c1")).Entries,
            row => row.DispatchRef == SlackStatusProjection.DispatchRef(source, "terminal"));
        var payload = SlackDeliveryPayload.Parse(terminal.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.PostMessage, payload.Operation);
        Assert.Null(payload.ProviderMessageIdentity);
    }

    [Fact]
    public async Task Accepted_terminal_delivery_is_retained_while_disabled_and_claimed_after_reenable()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        await SeedConnectionAsync(database, time, DesiredStateKind.Disabled);
        var store = CreateStore(database, time);
        var projection = new SlackStatusProjection(store);
        var source = new SlackMessageIdentity("T1", "C1", "100.001");

        var result = await projection.EnqueueTerminalAsync("p1", "c1", source, null, "completed", "done");

        Assert.False(result.Suppressed);
        Assert.Single((await store.ListAsync("p1", "c1")).Entries, entry => entry.State == SlackOutboxStates.Pending);
        Assert.Null(await store.ClaimAsync("p1", "c1", "adapter-1"));

        await using (var db = database.CreateContext())
        {
            var connection = await db.AgentConnections.SingleAsync(row => row.Id == "c1");
            connection.DesiredState = DesiredStateKind.Enabled;
            await db.SaveChangesAsync();
        }

        var claimed = await store.ClaimAsync("p1", "c1", "adapter-1");
        Assert.NotNull(claimed);
        Assert.Equal(SlackOutboxKinds.TerminalResult, claimed!.Kind);
    }

    [Fact]
    public async Task Deleted_connection_suppresses_new_status_delivery()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
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
        var result = await projection.EnqueueTerminalAsync(
            "p1", "c1", new SlackMessageIdentity("T1", "C1", "100.001"), null, "completed", "done");

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
