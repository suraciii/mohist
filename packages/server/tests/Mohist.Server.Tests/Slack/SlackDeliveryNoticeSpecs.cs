using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Project.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Slack;

/// <summary>
/// Delivery notices: the bounded, deduplicated system message that reports an
/// Agent reply or Session card whose delivery settled without a confirmed
/// outcome. The specs assert persisted row state and the notice payload the
/// adapter would transport — never only message text.
/// </summary>
public sealed partial class SlackDeliveryHandlerSpecs
{
    private const string NoticeSession = "session-notice-1";

    [Fact]
    public async Task Uncertain_agent_reply_gets_one_notice_with_the_session_reference()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        await CreateDmMappingAsync(database, time, connection, "C-notice-reply");
        var outbox = CreateStore(database, time);
        var notices = CreateNoticeAuthor(database, outbox);

        var reply = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "C-notice-reply",
            "1710000000.000020",
            "The deploy finished and the migration is verified.",
            connection.Id,
            "agent-session-followup:session-notice-1:turn-1",
            NoticeSession);
        await outbox.MarkDeliveryUncertainAsync(connection.ProjectId, reply.DeliveryId!, "claim timeout");

        Assert.True(await notices.TryNoticeAsync(
            connection.ProjectId, SlackDeliveryOwnerKinds.Connection, connection.Id, reply.DeliveryId!));

        var notice = Assert.Single(await NoticeRowsAsync(database, connection.Id));
        Assert.Equal(SlackOutboxKinds.ExplicitFailure, notice.Kind);
        Assert.Equal(SlackOutboxStates.Pending, notice.State);
        Assert.Equal($"{SlackDeliveryNoticeAuthor.DispatchPrefix}{reply.DeliveryId}", notice.DispatchRef);
        Assert.Equal("C-notice-reply", notice.ConversationId);
        Assert.Equal("1710000000.000020", notice.ThreadTs);

        var payload = SlackDeliveryPayload.Parse(notice.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.PostMessage, payload.Operation);
        Assert.Equal(SlackDeliveryNoticeAuthor.Uncertain, payload.Notice);
        Assert.True(payload.PossiblyDelivered);
        Assert.Equal(NoticeSession, payload.SessionId);
        Assert.Equal(notice.DispatchRef, payload.ClientMessageId);
        Assert.DoesNotContain("migration", payload.Text ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains($"Session: {NoticeSession}", payload.Text ?? string.Empty, StringComparison.Ordinal);
        AssertNoticeBlocks(payload);

        // The unknown outcome survives the visibility aid.
        var storedReply = await RowAsync(database, reply.DeliveryId!);
        Assert.Equal(SlackOutboxStates.DeliveryUncertain, storedReply.State);
        Assert.Equal(1, (await AllRowsAsync(database, connection.Id)).Count(row => row.DispatchRef == reply.DispatchRef));
    }

    [Fact]
    public async Task Uncertain_session_card_notice_names_the_card_and_leaves_the_reply_alone()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        await CreateDmMappingAsync(database, time, connection, "C-notice-card");
        var outbox = CreateStore(database, time);
        var notices = CreateNoticeAuthor(database, outbox);
        var projection = new SlackStatusProjection(outbox);
        var source = new SlackMessageIdentity(connection.WorkspaceTeamId, "C-notice-card", "1710000000.000030");
        var card = await projection.EnqueueWorkingAsync(
            connection.ProjectId, connection.Id, source, threadTs: null, sessionId: NoticeSession);
        var reply = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "C-notice-card",
            source.MessageTs,
            "Card delivery is not my problem.",
            connection.Id,
            "agent-session-followup:session-notice-1:turn-1",
            NoticeSession);
        await outbox.MarkDeliveredAsync(connection.ProjectId, reply.DeliveryId!);
        await outbox.MarkDeliveryUncertainAsync(connection.ProjectId, card.Id, "claim timeout");

        Assert.True(await notices.TryNoticeAsync(
            connection.ProjectId, SlackDeliveryOwnerKinds.Connection, connection.Id, card.Id));

        var notice = Assert.Single(await NoticeRowsAsync(database, connection.Id));
        Assert.Equal($"{SlackDeliveryNoticeAuthor.DispatchPrefix}{card.Id}", notice.DispatchRef);
        var payload = SlackDeliveryPayload.Parse(notice.PayloadJson);
        Assert.Contains("Session card", payload.Text ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("Agent reply", payload.Text ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(NoticeSession, payload.SessionId);

        // A card failure neither delays nor duplicates the confirmed reply.
        var storedReply = await RowAsync(database, reply.DeliveryId!);
        Assert.Equal(SlackOutboxStates.Delivered, storedReply.State);
        Assert.DoesNotContain(
            await AllRowsAsync(database, connection.Id),
            row => row.DispatchRef == $"{SlackDeliveryNoticeAuthor.DispatchPrefix}{reply.DeliveryId}");
    }

    [Fact]
    public async Task Claim_timeout_notice_is_authored_by_the_dispatcher_sweep()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        var outbox = CreateStore(database, time);
        var options = Options.Create(new SlackProviderOptions
        {
            OutboxClaimTimeout = TimeSpan.FromSeconds(30),
            OutboxUncertainTimeout = TimeSpan.FromMinutes(5),
        });
        var content = await outbox.EnqueueAsync(ContentDraft(connection, "agentjob_claim_timeout"));
        await outbox.ClaimAsync(connection.ProjectId, connection.Id, "adapter-a");

        time.Advance(TimeSpan.FromSeconds(31));
        var dispatcher = CreateDispatcher(database, time, options, outbox);
        await dispatcher.DispatchAsync(CancellationToken.None);

        var row = await RowAsync(database, content.Id);
        Assert.Equal(SlackOutboxStates.DeliveryUncertain, row.State);
        var notice = Assert.Single(await NoticeRowsAsync(database, connection.Id));
        var payload = SlackDeliveryPayload.Parse(notice.PayloadJson);
        Assert.Equal(SlackDeliveryNoticeAuthor.Uncertain, payload.Notice);
        Assert.True(payload.PossiblyDelivered);
    }

    [Fact]
    public async Task Uncertain_timeout_dead_letter_keeps_the_possibly_delivered_fact()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        var outbox = CreateStore(database, time);
        var options = Options.Create(new SlackProviderOptions
        {
            OutboxClaimTimeout = TimeSpan.FromSeconds(30),
            OutboxUncertainTimeout = TimeSpan.FromMinutes(5),
        });
        var content = await outbox.EnqueueAsync(ContentDraft(connection, "agentjob_uncertain_timeout"));
        await outbox.MarkDeliveryUncertainAsync(connection.ProjectId, content.Id, "claim timeout");

        // No notice was reachable while Slack was unavailable; the timeout must
        // not turn the unknown outcome into a definite failure.
        time.Advance(TimeSpan.FromMinutes(6));
        var dispatcher = CreateDispatcher(database, time, options, outbox);
        await dispatcher.DispatchAsync(CancellationToken.None);

        var row = await RowAsync(database, content.Id);
        Assert.Equal(SlackOutboxStates.DeadLettered, row.State);
        var notice = Assert.Single(await NoticeRowsAsync(database, connection.Id));
        var payload = SlackDeliveryPayload.Parse(notice.PayloadJson);
        Assert.Equal(SlackDeliveryNoticeAuthor.Exhausted, payload.Notice);
        Assert.True(payload.PossiblyDelivered);
    }

    [Fact]
    public async Task Retry_exhausted_dead_letter_reports_generated_but_not_delivered()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        var options = Options.Create(new SlackProviderOptions
        {
            OutboxClaimTimeout = TimeSpan.FromSeconds(30),
            OutboxUncertainTimeout = TimeSpan.FromMinutes(5),
            OutboxMaxAttempts = 2,
        });
        var outbox = CreateStore(database, time, options: options);
        var content = await outbox.EnqueueAsync(ContentDraft(connection, "agentjob_rejected"));
        await outbox.ScheduleRetryAsync(connection.ProjectId, content.Id, "channel_not_found");
        await outbox.ScheduleRetryAsync(connection.ProjectId, content.Id, "channel_not_found");

        var dispatcher = CreateDispatcher(database, time, options, outbox);
        await dispatcher.DispatchAsync(CancellationToken.None);

        var row = await RowAsync(database, content.Id);
        Assert.Equal(SlackOutboxStates.DeadLettered, row.State);
        var notice = Assert.Single(await NoticeRowsAsync(database, connection.Id));
        var payload = SlackDeliveryPayload.Parse(notice.PayloadJson);
        Assert.Equal(SlackDeliveryNoticeAuthor.Exhausted, payload.Notice);
        Assert.False(payload.PossiblyDelivered);
        Assert.NotEqual(
            SlackDeliveryNoticeAuthor.Render(
                SlackDeliveryNoticeAuthor.Subject.AgentReply, SlackDeliveryNoticeAuthor.Uncertain, true, NoticeSession),
            SlackDeliveryNoticeAuthor.Render(
                SlackDeliveryNoticeAuthor.Subject.AgentReply, SlackDeliveryNoticeAuthor.Exhausted, false, NoticeSession));
    }

    [Fact]
    public async Task Repeated_unknown_acknowledgements_and_restarts_keep_one_notice()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        var outbox = CreateStore(database, time);
        var notices = CreateNoticeAuthor(database, outbox);
        const string dispatchRef = "agentjob_replayed";
        var content = await outbox.EnqueueAsync(ContentDraft(connection, dispatchRef));

        for (var replay = 0; replay < 3; replay++)
        {
            await outbox.MarkDeliveryUncertainAsync(connection.ProjectId, content.Id, "uncertain ack");
            await notices.TryNoticeAsync(
                connection.ProjectId, SlackDeliveryOwnerKinds.Connection, connection.Id, content.Id);
        }

        Assert.Single(await NoticeRowsAsync(database, connection.Id));

        // A restart reruns the sweep over the same settled row.
        var options = Options.Create(new SlackProviderOptions
        {
            OutboxClaimTimeout = TimeSpan.FromSeconds(30),
            OutboxUncertainTimeout = TimeSpan.FromMinutes(5),
        });
        var dispatcher = CreateDispatcher(database, time, options, outbox);
        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Single(await NoticeRowsAsync(database, connection.Id));
        Assert.Single(await AllRowsAsync(database, connection.Id), row => row.DispatchRef == dispatchRef);
    }

    [Fact]
    public async Task Notice_never_produces_a_notice_about_itself()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        var outbox = CreateStore(database, time);
        var notices = CreateNoticeAuthor(database, outbox);
        const string dispatchRef = "agentjob_notice_exhaustion";
        var content = await outbox.EnqueueAsync(ContentDraft(connection, dispatchRef));
        await outbox.MarkDeliveryUncertainAsync(connection.ProjectId, content.Id, "claim timeout");
        await notices.TryNoticeAsync(
            connection.ProjectId, SlackDeliveryOwnerKinds.Connection, connection.Id, content.Id);
        var notice = Assert.Single(await NoticeRowsAsync(database, connection.Id));

        await outbox.MarkDeadLetteredAsync(
            connection.ProjectId,
            notice.Id,
            "retry budget exhausted",
            expectedState: SlackOutboxStates.Pending,
            expectedUpdatedAt: notice.UpdatedAt);
        Assert.False(await notices.TryNoticeAsync(
            connection.ProjectId, SlackDeliveryOwnerKinds.Connection, connection.Id, notice.Id));

        Assert.Single(await NoticeRowsAsync(database, connection.Id));
        Assert.Single(await AllRowsAsync(database, connection.Id), row => row.DispatchRef == dispatchRef);
    }

    [Fact]
    public async Task Retrying_and_delivered_deliveries_get_no_notice()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        var outbox = CreateStore(database, time);
        var notices = CreateNoticeAuthor(database, outbox);
        var retrying = await outbox.EnqueueAsync(ContentDraft(connection, "agentjob_retrying"));
        var delivered = await outbox.EnqueueAsync(ContentDraft(connection, "agentjob_delivered"));
        await outbox.ScheduleRetryAsync(connection.ProjectId, retrying.Id, "rate_limited");
        await outbox.MarkDeliveredAsync(connection.ProjectId, delivered.Id);

        Assert.False(await notices.TryNoticeAsync(
            connection.ProjectId, SlackDeliveryOwnerKinds.Connection, connection.Id, retrying.Id));
        Assert.False(await notices.TryNoticeAsync(
            connection.ProjectId, SlackDeliveryOwnerKinds.Connection, connection.Id, delivered.Id));

        Assert.Empty(await NoticeRowsAsync(database, connection.Id));
        Assert.Equal(SlackOutboxStates.Pending, (await RowAsync(database, retrying.Id)).State);
        Assert.Equal(SlackOutboxStates.Delivered, (await RowAsync(database, delivered.Id)).State);
    }

    [Fact]
    public async Task Agent_re_send_of_an_exhausted_reply_returns_it_to_reconciliation()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        await CreateDmMappingAsync(database, time, connection, "C-notice-resend");
        var outbox = CreateStore(database, time);
        const string text = "The migration is verified.";
        var first = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "C-notice-resend",
            "1710000000.000040",
            text,
            connection.Id,
            "agent-session-followup:session-notice-1:turn-1",
            NoticeSession);
        var queued = await RowAsync(database, first.DeliveryId!);
        await outbox.MarkDeadLetteredAsync(
            connection.ProjectId,
            first.DeliveryId!,
            "retry budget exhausted",
            expectedState: SlackOutboxStates.Pending,
            expectedUpdatedAt: queued.UpdatedAt);

        var repeated = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "C-notice-resend",
            "1710000000.000040",
            text,
            connection.Id,
            "agent-session-followup:session-notice-1:turn-1",
            NoticeSession);

        Assert.True(repeated.Accepted);
        Assert.True(repeated.MergedIntoExisting);
        Assert.False(repeated.ConflictingDuplicate);
        Assert.True(repeated.RequeuedForReconciliation);
        var revived = await RowAsync(database, first.DeliveryId!);
        Assert.Equal(SlackOutboxStates.DeliveryUncertain, revived.State);
        Assert.Null(revived.DeadLetteredAt);
        Assert.NotNull(revived.DeliveryUncertainAt);
        Assert.Single(await AllRowsAsync(database, connection.Id));

        var conflicting = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "C-notice-resend",
            "1710000000.000040",
            "A different answer for the same turn.",
            connection.Id,
            "agent-session-followup:session-notice-1:turn-1",
            NoticeSession);
        Assert.True(conflicting.ConflictingDuplicate);
        Assert.Equal(SlackOutboxStates.DeliveryUncertain, (await RowAsync(database, first.DeliveryId!)).State);
    }

    private static SlackOutboxDraft ContentDraft(AgentConnection connection, string dispatchRef) =>
        new(
            connection.ProjectId,
            connection.Id,
            connection.WorkspaceTeamId,
            "D1",
            SlackOutboxKinds.TerminalResult,
            dispatchRef,
            JsonSerializer.Serialize(new SlackDeliveryPayload(
                SlackDeliveryOperations.PostMessage,
                "generated content",
                ClientMessageId: dispatchRef,
                FallbackText: "generated content",
                SessionId: NoticeSession)));

    private static SlackDeliveryNoticeAuthor CreateNoticeAuthor(
        TestSqliteDatabase database,
        SlackOutboxStore outbox) =>
        new(
            outbox,
            new SlackSessionCardBlocksBuilder(
                new SlackWebLinkBuilder(Options.Create(new SlackProviderOptions())),
                new ProjectQuerier(new TestDbContextFactory(database.Options))),
            NullLogger<SlackDeliveryNoticeAuthor>.Instance);

    private static SlackOutboxDispatcherService CreateDispatcher(
        TestSqliteDatabase database,
        FakeTimeProvider time,
        IOptions<SlackProviderOptions> options,
        SlackOutboxStore outbox)
    {
        var factory = new TestDbContextFactory(database.Options);
        var health = new NoopHealthBackpressurer();
        return new SlackOutboxDispatcherService(
            outbox,
            new SlackProviderInboxStore(factory, time, options, health),
            new AgentConnectionStore(
                factory,
                new AgentQuerier(factory),
                new NoopSecretStore(),
                Array.Empty<IAgentConnectionProviderCleanup>(),
                time),
            health,
            new NoopDeadLetterStore(),
            CreateNoticeAuthor(database, outbox),
            time,
            options,
            NullLogger<SlackOutboxDispatcherService>.Instance);
    }

    private static async Task<IReadOnlyList<SlackOutboxRow>> AllRowsAsync(
        TestSqliteDatabase database,
        string connectionId)
    {
        await using var db = database.CreateContext();
        var rows = await db.SlackOutboxRows.AsNoTracking()
            .Where(row => row.ConnectionId == connectionId)
            .ToListAsync();
        return rows.OrderBy(row => row.Id, StringComparer.Ordinal).ToList();
    }

    private static async Task<IReadOnlyList<SlackOutboxRow>> NoticeRowsAsync(
        TestSqliteDatabase database,
        string connectionId) =>
        (await AllRowsAsync(database, connectionId))
            .Where(row => row.DispatchRef?.StartsWith(SlackDeliveryNoticeAuthor.DispatchPrefix, StringComparison.Ordinal) == true)
            .ToList();

    private static async Task<SlackOutboxRow> RowAsync(TestSqliteDatabase database, string id)
    {
        await using var db = database.CreateContext();
        return await db.SlackOutboxRows.AsNoTracking().SingleAsync(row => row.Id == id);
    }

    private static void AssertNoticeBlocks(SlackDeliveryPayload payload)
    {
        Assert.NotNull(payload.Blocks);
        var blocks = payload.Blocks!.Value;
        Assert.Equal(JsonValueKind.Array, blocks.ValueKind);
        var rendered = blocks.GetRawText();
        Assert.Contains($"Session: {NoticeSession}", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\"actions\"", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("action_id", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", rendered, StringComparison.Ordinal);
    }

    private sealed class NoopSecretStore : ISecretStore
    {
        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult(false);
        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }
}
