using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L0")]
public sealed class SlackOutboxProgressAtomicityTests
{
    [Fact]
    public async Task Claim_wins_over_a_stale_session_card_upsert()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
        await SeedConnectionAsync(database, time);
        var store = CreateStore(database.Options, time);
        var first = Draft("Agent session.\nSession: session-original");
        var card = await store.UpsertReplaceableProgressAsync(first);

        var gate = new PendingPayloadUpdateGate();
        var racingOptions = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(database.Keeper.ConnectionString)
            .AddInterceptors(gate)
            .Options;
        var racingStore = CreateStore(racingOptions, time);
        var replay = racingStore.UpsertReplaceableProgressAsync(
            Draft("Agent session.\nSession: session-replayed"));

        await gate.UpdateReached.Task;
        var claimed = await store.ClaimAsync("p1", "c1", "adapter-1");
        Assert.Equal(card.Id, claimed?.Id);
        gate.ReleaseUpdate();
        var replayResult = await replay;

        Assert.True(replayResult.MergedIntoExisting);
        Assert.Equal(card.Id, replayResult.Id);
        var persisted = Assert.Single((await store.ListAsync("p1", "c1")).Entries);
        Assert.Equal(SlackOutboxStates.Claimed, persisted.State);
        Assert.Equal(
            "Agent session.\nSession: session-original",
            SlackDeliveryPayload.Parse(persisted.PayloadJson).Text);
    }

    private static SlackOutboxDraft Draft(string text) =>
        new(
            "p1",
            "c1",
            "T1",
            "C1",
            SlackOutboxKinds.ReplaceableProgress,
            "session-card:1",
            JsonSerializer.Serialize(new SlackDeliveryPayload(
                SlackDeliveryOperations.PostMessage,
                text,
                ClientMessageId: "session-card:1")));

    private static SlackOutboxStore CreateStore(
        DbContextOptions<MohistDbContext> options,
        FakeTimeProvider time) =>
        new(
            new TestDbContextFactory(options),
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

    private sealed class PendingPayloadUpdateGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _updateReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseUpdate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;

        public TaskCompletionSource UpdateReached => _updateReached;

        public void ReleaseUpdate() => _releaseUpdate.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE \"SlackOutboxRows\"", StringComparison.Ordinal)
                && command.CommandText.Contains("\"PayloadJson\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                _updateReached.TrySetResult();
                await _releaseUpdate.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class NoopHealthBackpressurer : ISlackConnectionHealthBackpressurer
    {
        public Task FlipBackpressuredAsync(
            string projectId,
            string connectionId,
            string reason,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<int> RecoverBackpressuredAsync(
            string projectId,
            string connectionId,
            CancellationToken ct = default) => Task.FromResult(0);
    }
}
