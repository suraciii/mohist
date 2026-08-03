using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.SpecTests.Support;
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
        await projection.EnqueueTerminalAsync("p1", "c1", source, "100.000", "failed", "failed");

        var terminal = Assert.Single((await store.ListAsync("p1", "c1")).Entries, row => row.Kind == SlackOutboxKinds.ExplicitFailure);
        var payload = SlackDeliveryPayload.Parse(terminal.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.ChatUpdate, payload.Operation);
        Assert.Equal(new SlackProviderMessageIdentity("C1", "100.002"), payload.ProviderMessageIdentity);
    }

    private static SlackOutboxStore CreateStore(TestSqliteDatabase database, FakeTimeProvider time) =>
        new(
            new TestDbContextFactory(database.Options),
            new NoopHealthBackpressurer(),
            time,
            Options.Create(new SlackProviderOptions { OutboxCapacityPerConnection = 20 }));

    private static async Task SeedConnectionAsync(TestSqliteDatabase database, FakeTimeProvider time)
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
            DesiredState = DesiredStateKind.Enabled,
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
