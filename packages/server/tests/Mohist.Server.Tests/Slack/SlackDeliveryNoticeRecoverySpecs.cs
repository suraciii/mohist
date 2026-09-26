using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Slack;

/// <summary>
/// The notice recovery sweep is bounded, so its candidate selection decides
/// which owners make progress. A row that cannot be authored right now — an
/// owner that is gone, or a payload that no longer parses — must not keep the
/// first slots busy while other owners' visibility obligations wait.
/// </summary>
public sealed partial class SlackDeliveryHandlerSpecs
{
    [Fact]
    public async Task Notice_recovery_skips_owners_that_cannot_receive_a_notice()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var options = Options.Create(new SlackProviderOptions { DispatcherBatchSize = 1 });
        var outbox = CreateStore(database, time, options: options);
        var goneOwner = await CreateConnectionAsync(database, time);
        var liveOwner = await CreateConnectionAsync(database, time);
        await AddSettledContentRowAsync(database, "000-recovery-gone-owner", goneOwner, "recovery-gone-owner");
        await AddSettledContentRowAsync(database, "zzz-recovery-live-owner", liveOwner, "recovery-live-owner");
        await SetConnectionLiveAsync(database, goneOwner, live: false);

        // The single slot goes to the owner that can receive a notice, even
        // though the owner that is gone sorts first and also owes one.
        using var dispatcher = CreateDispatcher(database, time, options, outbox);
        await dispatcher.DispatchAsync(CancellationToken.None);
        Assert.Single(await NoticeRowsAsync(database, liveOwner.Id));
        Assert.Empty(await NoticeRowsAsync(database, goneOwner.Id));

        // The skipped row keeps its obligation: once the owner can receive
        // messages again the same row is authored.
        var stillOwed = await RowAsync(database, "000-recovery-gone-owner");
        Assert.Equal(SlackOutboxStates.DeadLettered, stillOwed.State);
        await SetConnectionLiveAsync(database, goneOwner, live: true);
        await dispatcher.DispatchAsync(CancellationToken.None);
        await dispatcher.DispatchAsync(CancellationToken.None);
        var notice = Assert.Single(await NoticeRowsAsync(database, goneOwner.Id));
        Assert.Equal(
            $"{SlackDeliveryNoticeAuthor.DispatchPrefix}000-recovery-gone-owner",
            notice.DispatchRef);
    }

    [Fact]
    public async Task Notice_recovery_keeps_moving_past_a_candidate_that_cannot_be_authored()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var options = Options.Create(new SlackProviderOptions { DispatcherBatchSize = 1 });
        var outbox = CreateStore(database, time, options: options);
        var unreadableOwner = await CreateConnectionAsync(database, time);
        var healthyOwner = await CreateConnectionAsync(database, time);
        await AddSettledContentRowAsync(
            database,
            "000-recovery-unreadable",
            unreadableOwner,
            "recovery-unreadable",
            payloadJson: "{ not a delivery payload");
        await AddSettledContentRowAsync(database, "zzz-recovery-healthy", healthyOwner, "recovery-healthy");

        using var dispatcher = CreateDispatcher(database, time, options, outbox);
        await dispatcher.DispatchAsync(CancellationToken.None);
        Assert.Empty(await NoticeRowsAsync(database, healthyOwner.Id));

        // The next tick resumes after the row it already examined instead of
        // re-selecting it, so the healthy owner's notice is still authored.
        await dispatcher.DispatchAsync(CancellationToken.None);
        var notice = Assert.Single(await NoticeRowsAsync(database, healthyOwner.Id));
        Assert.Equal(
            $"{SlackDeliveryNoticeAuthor.DispatchPrefix}zzz-recovery-healthy",
            notice.DispatchRef);
        Assert.Empty(await NoticeRowsAsync(database, unreadableOwner.Id));
    }

    /// <summary>
    /// Adds one already-settled content row with a fixed id, so the tests can
    /// pin the order the recovery sweep examines candidates in.
    /// </summary>
    private static async Task AddSettledContentRowAsync(
        TestSqliteDatabase database,
        string id,
        AgentConnection connection,
        string dispatchRef,
        string? payloadJson = null)
    {
        await using var db = database.CreateContext();
        db.SlackOutboxRows.Add(new SlackOutboxRow
        {
            Id = id,
            ProjectId = connection.ProjectId,
            ConnectionId = connection.Id,
            OwnerKind = SlackDeliveryOwnerKinds.Connection,
            WorkspaceTeamId = connection.WorkspaceTeamId,
            ConversationId = "D1",
            Kind = SlackOutboxKinds.TerminalResult,
            State = SlackOutboxStates.DeadLettered,
            DispatchRef = dispatchRef,
            PayloadJson = payloadJson ?? JsonSerializer.Serialize(new SlackDeliveryPayload(
                SlackDeliveryOperations.PostMessage,
                "generated content",
                ClientMessageId: dispatchRef,
                FallbackText: "generated content",
                SessionId: NoticeSession)),
            DeadLetteredAt = Start,
            CreatedAt = Start,
            UpdatedAt = Start,
        });
        await db.SaveChangesAsync();
    }
}
