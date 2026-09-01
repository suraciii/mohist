using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L0")]
public sealed partial class SlackDeliveryHandlerSpecs
{
    private static readonly DateTimeOffset Start = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Delivery_outcome_retry_reschedules_explicit_failure_without_marking_uncertain()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        var outbox = CreateStore(database, time);
        var queued = await outbox.EnqueueAsync(new SlackOutboxDraft(
            connection.ProjectId,
            connection.Id,
            connection.WorkspaceTeamId,
            "D1",
            SlackOutboxKinds.TerminalResult,
            "agentjob_42",
            "{\"text\":\"reply\"}"));
        await outbox.MarkDeliveredAsync(connection.ProjectId, queued.Id);
        await outbox.ScheduleRetryAsync(connection.ProjectId, queued.Id, "channel_not_found");

        await using var verifyDb = database.CreateContext();
        var row = await verifyDb.SlackOutboxRows.AsNoTracking()
            .SingleAsync(r => r.Id == queued.Id);
        Assert.Equal(SlackOutboxStates.Pending, row.State);
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal("channel_not_found", row.LastError);
    }
    [Fact]
    public async Task ResendUncertain_only_advances_delivery_uncertain_rows()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        var outbox = CreateStore(database, time);
        var pending = await outbox.EnqueueAsync(new SlackOutboxDraft(
            connection.ProjectId,
            connection.Id,
            connection.WorkspaceTeamId,
            "D1",
            SlackOutboxKinds.TerminalResult,
            "agentjob_pending",
            "{\"text\":\"still pending\"}"));
        var uncertain = await outbox.EnqueueAsync(new SlackOutboxDraft(
            connection.ProjectId,
            connection.Id,
            connection.WorkspaceTeamId,
            "D1",
            SlackOutboxKinds.TerminalResult,
            "agentjob_uncertain",
            "{\"text\":\"uncertain\"}"));
        await outbox.MarkDeliveryUncertainAsync(connection.ProjectId, uncertain.Id, "claim timeout");

        var resendResult = await outbox.ResendUncertainAsync(connection.ProjectId, connection.Id, uncertain.Id);
        Assert.Equal(1, resendResult);

        var notUncertainResult = await outbox.ResendUncertainAsync(connection.ProjectId, connection.Id, pending.Id);
        Assert.Equal(0, notUncertainResult);

        await using var verifyDb = database.CreateContext();
        var advanced = await verifyDb.SlackOutboxRows.AsNoTracking()
            .SingleAsync(r => r.Id == uncertain.Id);
        Assert.Equal(SlackOutboxStates.Pending, advanced.State);
        Assert.Null(advanced.DeliveryUncertainAt);

        var untouched = await verifyDb.SlackOutboxRows.AsNoTracking()
            .SingleAsync(r => r.Id == pending.Id);
        Assert.Equal(SlackOutboxStates.Pending, untouched.State);
    }
    [Fact]
    public async Task Agent_reply_is_independent_from_the_liveness_session_card()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        await CreateDmMappingAsync(database, time, connection, "C-reply-inplace");
        var outbox = CreateStore(database, time);
        var projection = new SlackStatusProjection(outbox);
        var source = new SlackMessageIdentity(connection.WorkspaceTeamId, "C-reply-inplace", "1710000000.000010");
        var working = await projection.EnqueueWorkingAsync(connection.ProjectId, connection.Id, source, threadTs: null);

        var reply = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId, "C-reply-inplace", null, "All green — the task is complete.");

        Assert.True(reply.Accepted);
        Assert.Equal(connection.Id, reply.ConnectionId);
        Assert.NotEqual(working.Id, reply.DeliveryId);
        Assert.False(reply.MergedIntoExisting);
        var rows = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries;
        var card = Assert.Single(rows, r => r.Id == working.Id);
        Assert.Equal(SlackOutboxKinds.ReplaceableProgress, card.Kind);
        var terminal = Assert.Single(rows, r => r.Kind == SlackOutboxKinds.TerminalResult);
        Assert.Equal(reply.DeliveryId, terminal.Id);
        var payload = SlackDeliveryPayload.Parse(terminal.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.PostMessage, payload.Operation);
        Assert.Contains("the task is complete", payload.Text, StringComparison.Ordinal);
    }
    [Fact]
    public async Task Agent_reply_merges_repeated_sends_into_one_terminal_answer()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        var outbox = CreateStore(database, time);
        var projection = new SlackStatusProjection(outbox);
        var source = new SlackMessageIdentity(connection.WorkspaceTeamId, "C-reply-merge", "1710000000.000020");
        await CreateDmMappingAsync(connection, "C-reply-merge");
        var working = await projection.EnqueueWorkingAsync(connection.ProjectId, connection.Id, source, threadTs: null);

        var first = await outbox.EnqueueAgentReplyAsync(connection.ProjectId, "C-reply-merge", null, "part one");
        var second = await outbox.EnqueueAgentReplyAsync(connection.ProjectId, "C-reply-merge", null, "part two");

        Assert.True(first.Accepted);
        Assert.Equal(first.DeliveryId, second.DeliveryId);
        var rows = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries;
        Assert.Contains(rows, row => row.Id == working.Id
            && row.Kind == SlackOutboxKinds.ReplaceableProgress);
        var terminal = Assert.Single(rows, r => r.Kind == SlackOutboxKinds.TerminalResult);
        Assert.NotEqual(working.Id, terminal.Id);
        var payload = SlackDeliveryPayload.Parse(terminal.PayloadJson);
        Assert.Contains("part one", payload.Text, StringComparison.Ordinal);
        Assert.Contains("part two", payload.Text, StringComparison.Ordinal);
    }
    [Fact]
    public async Task Agent_reply_dispatch_identity_separates_turns_and_keeps_retries_idempotent()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        await CreateDmMappingAsync(database, time, connection, "D-reply-turns");
        var outbox = CreateStore(database, time);

        var first = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-turns",
            null,
            "ACK",
            replyDispatchRef: "agent-session-followup:session-1:turn-1");
        var retry = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-turns",
            null,
            "ACK",
            replyDispatchRef: "agent-session-followup:session-1:turn-1");
        var providerIdentity = new SlackProviderMessageIdentity("D-reply-turns", "1710000000.000500");
        await outbox.MarkDeliveredAsync(connection.ProjectId, first.DeliveryId!, providerIdentity);
        var deliveredRetry = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-turns",
            null,
            "ACK",
            replyDispatchRef: "agent-session-followup:session-1:turn-1");
        var deliveredDistinct = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-turns",
            null,
            "more detail",
            replyDispatchRef: "agent-session-followup:session-1:turn-1");
        var latestRetry = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-turns",
            null,
            "more detail",
            replyDispatchRef: "agent-session-followup:session-1:turn-1");
        var secondTurn = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-turns",
            null,
            "531",
            replyDispatchRef: "agent-session-followup:session-1:turn-2");

        Assert.Equal(first.DeliveryId, retry.DeliveryId);
        Assert.Equal(first.DeliveryId, deliveredRetry.DeliveryId);
        Assert.Equal(first.DeliveryId, deliveredDistinct.DeliveryId);
        Assert.Equal(first.DeliveryId, latestRetry.DeliveryId);
        Assert.NotEqual(first.DeliveryId, secondTurn.DeliveryId);
        Assert.False(secondTurn.MergedIntoExisting);

        var replies = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries
            .Where(row => row.Kind == SlackOutboxKinds.TerminalResult)
            .ToArray();
        Assert.Equal(2, replies.Length);
        var firstReply = Assert.Single(
            replies,
            row => row.DispatchRef == "slack-reply:agent-session-followup:session-1:turn-1:terminal");
        var secondReply = Assert.Single(
            replies,
            row => row.DispatchRef == "slack-reply:agent-session-followup:session-1:turn-2:terminal");
        Assert.Equal(SlackOutboxStates.Pending, firstReply.State);
        var firstPayload = SlackDeliveryPayload.Parse(firstReply.PayloadJson);
        Assert.Equal("ACK\n\nmore detail", firstPayload.Text);
        Assert.Equal(["ACK", "more detail"], firstPayload.ReplyParts);
        Assert.Equal(firstPayload.Text, firstPayload.FallbackText);
        Assert.Equal(SlackDeliveryOperations.ChatUpdate, firstPayload.Operation);
        Assert.Equal(providerIdentity, firstPayload.ProviderMessageIdentity);
        Assert.Equal("531", SlackDeliveryPayload.Parse(secondReply.PayloadJson).Text);
    }
    [Fact]
    public async Task Anchored_retry_recognizes_the_latest_part_of_a_pre_reply_parts_payload()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        await CreateDmMappingAsync(database, time, connection, "D-reply-legacy-parts");
        var outbox = CreateStore(database, time);
        const string dispatchRef = "agent-session-followup:legacy-parts:turn-1";

        await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId, "D-reply-legacy-parts", null, "part one", replyDispatchRef: dispatchRef);
        await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId, "D-reply-legacy-parts", null, "part two", replyDispatchRef: dispatchRef);

        await using var db = database.CreateContext();
        var row = await db.SlackOutboxRows.SingleAsync(candidate =>
            candidate.ProjectId == connection.ProjectId && candidate.ConversationId == "D-reply-legacy-parts");
        row.PayloadJson = JsonSerializer.Serialize(SlackDeliveryPayload.Parse(row.PayloadJson) with { ReplyParts = null });
        await db.SaveChangesAsync();

        var retry = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId, "D-reply-legacy-parts", null, "part two", replyDispatchRef: dispatchRef);

        Assert.True(retry.Accepted);
        var persisted = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries.Single();
        Assert.Equal("part one\n\npart two", SlackDeliveryPayload.Parse(persisted.PayloadJson).Text);
    }
    [Fact]
    public async Task Authoritative_reply_parts_do_not_treat_a_paragraph_suffix_as_a_retry()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        await CreateDmMappingAsync(database, time, connection, "D-reply-authoritative-parts");
        var outbox = CreateStore(database, time);
        const string dispatchRef = "agent-session-followup:authoritative-parts:turn-1";

        await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-authoritative-parts",
            null,
            "summary\n\nnext",
            replyDispatchRef: dispatchRef);
        await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-authoritative-parts",
            null,
            "next",
            replyDispatchRef: dispatchRef);

        var row = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries.Single();
        var payload = SlackDeliveryPayload.Parse(row.PayloadJson);
        Assert.Equal("summary\n\nnext\n\nnext", payload.Text);
        Assert.Equal(["summary\n\nnext", "next"], payload.ReplyParts);
    }
    [Fact]
    public async Task Explicit_reply_reuses_identical_in_flight_retry_and_rejects_different_content()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        await CreateDmMappingAsync(database, time, connection, "D-reply-in-flight");
        var outbox = CreateStore(database, time);
        const string replyDispatchRef = "agent-session-followup:session-1:turn-in-flight";

        var first = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-in-flight",
            null,
            "original",
            replyDispatchRef: replyDispatchRef);
        var claimed = await outbox.ClaimAsync(connection.ProjectId, connection.Id, "adapter-in-flight");
        Assert.Equal(first.DeliveryId, claimed?.Id);

        var claimedRetry = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-in-flight",
            null,
            "original",
            replyDispatchRef: replyDispatchRef);
        var claimedAddition = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-in-flight",
            null,
            "claimed addition",
            replyDispatchRef: replyDispatchRef);
        Assert.True(claimedRetry.Accepted);
        Assert.False(claimedAddition.Accepted);
        Assert.True(claimedAddition.ConflictingDuplicate);
        var claimedRow = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries.Single();
        Assert.Equal(SlackOutboxStates.Claimed, claimedRow.State);
        Assert.Equal("original", SlackDeliveryPayload.Parse(claimedRow.PayloadJson).Text);

        await outbox.MarkDeliveryUncertainAsync(
            connection.ProjectId, claimedRow.Id, "provider response lost", "adapter-in-flight");
        var uncertainRetry = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-in-flight",
            null,
            "original",
            replyDispatchRef: replyDispatchRef);
        var uncertainAddition = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-in-flight",
            null,
            "uncertain addition",
            replyDispatchRef: replyDispatchRef);
        Assert.True(uncertainRetry.Accepted);
        Assert.False(uncertainAddition.Accepted);
        Assert.True(uncertainAddition.ConflictingDuplicate);
        var uncertainRow = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries.Single();
        Assert.Equal(SlackOutboxStates.DeliveryUncertain, uncertainRow.State);
        Assert.Equal("original", SlackDeliveryPayload.Parse(uncertainRow.PayloadJson).Text);
    }
    [Fact]
    public async Task Anchored_agent_reply_does_not_mutate_another_connection_session_card()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var first = await CreateConnectionAsync(database, time);
        var second = await CreateConnectionAsync(database, time, first.ProjectId, "agent-2");
        await CreateThreadMappingAsync(database, time, first, "C-shared-progress", "1710000000.000025");
        await CreateThreadMappingAsync(database, time, second, "C-shared-progress", "1710000000.000025");
        var outbox = CreateStore(database, time);
        var projection = new SlackStatusProjection(outbox);
        var source = new SlackMessageIdentity(first.WorkspaceTeamId, "C-shared-progress", "1710000000.000026");
        var firstProgress = await projection.EnqueueWorkingAsync(
            first.ProjectId, first.Id, source, "1710000000.000025", "first:progress");
        var secondProgress = await projection.EnqueueWorkingAsync(
            second.ProjectId, second.Id, source, "1710000000.000025", "second:progress");

        var reply = await outbox.EnqueueAgentReplyAsync(
            first.ProjectId,
            "C-shared-progress",
            "1710000000.000025",
            "second answer",
            connectionId: second.Id,
            replyDispatchRef: "second:turn");

        Assert.True(reply.Accepted);
        Assert.Equal(second.Id, reply.ConnectionId);
        Assert.NotEqual(secondProgress.Id, reply.DeliveryId);
        var firstRows = (await outbox.ListAsync(first.ProjectId, first.Id)).Entries;
        Assert.Contains(firstRows, row => row.Id == firstProgress.Id
            && row.Kind == SlackOutboxKinds.ReplaceableProgress);
        var secondRows = (await outbox.ListAsync(second.ProjectId, second.Id)).Entries;
        Assert.Contains(secondRows, row => row.Id == secondProgress.Id
            && row.Kind == SlackOutboxKinds.ReplaceableProgress);
        Assert.Contains(secondRows, row => row.Id == reply.DeliveryId
            && row.Kind == SlackOutboxKinds.TerminalResult);
    }
    [Fact]
    public async Task Anchored_agent_reply_does_not_mutate_other_turn_session_cards()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        await CreateDmMappingAsync(database, time, connection, "D-shared-turns");
        var outbox = CreateStore(database, time);
        var projection = new SlackStatusProjection(outbox);
        var firstSource = new SlackMessageIdentity(
            connection.WorkspaceTeamId, "D-shared-turns", "1710000000.000031");
        var secondSource = new SlackMessageIdentity(
            connection.WorkspaceTeamId, "D-shared-turns", "1710000000.000032");
        var firstProgress = await projection.EnqueueWorkingAsync(
            connection.ProjectId, connection.Id, firstSource, null, "first-turn:progress");
        var secondProgress = await projection.EnqueueWorkingAsync(
            connection.ProjectId, connection.Id, secondSource, null, "second-turn:progress");

        var reply = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-shared-turns",
            firstSource.MessageTs,
            "second turn answer",
            connectionId: connection.Id,
            replyDispatchRef: "second-turn:reply");

        Assert.True(reply.Accepted);
        Assert.NotEqual(secondProgress.Id, reply.DeliveryId);
        var rows = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries;
        Assert.Contains(rows, row => row.Id == firstProgress.Id
            && row.Kind == SlackOutboxKinds.ReplaceableProgress);
        Assert.Contains(rows, row => row.Id == secondProgress.Id
            && row.Kind == SlackOutboxKinds.ReplaceableProgress);
        Assert.Contains(rows, row => row.Id == reply.DeliveryId
            && row.Kind == SlackOutboxKinds.TerminalResult);
    }
    [Fact]
    public async Task Anchored_agent_replies_scope_terminal_and_attachment_routing_to_the_requested_connection()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var first = await CreateConnectionAsync(database, time);
        var second = await CreateConnectionAsync(database, time, first.ProjectId, "agent-2");
        await CreateThreadMappingAsync(database, time, first, "C-shared-terminal", "1710000000.000027");
        await CreateThreadMappingAsync(database, time, second, "C-shared-terminal", "1710000000.000027");
        var outbox = CreateStore(database, time);

        var firstReply = await outbox.EnqueueAgentReplyAsync(
            first.ProjectId,
            "C-shared-terminal",
            "1710000000.000027",
            "first answer",
            connectionId: first.Id,
            replyDispatchRef: "shared:turn");
        var secondReply = await outbox.EnqueueAgentReplyAsync(
            second.ProjectId,
            "C-shared-terminal",
            "1710000000.000027",
            "second answer",
            connectionId: second.Id,
            replyDispatchRef: "shared:turn");
        var secondImage = await outbox.EnqueueAgentReplyAsync(
            second.ProjectId,
            "C-shared-terminal",
            "1710000000.000027",
            "second image",
            connectionId: second.Id,
            replyDispatchRef: "shared:image-turn",
            imageUrl: "https://example.com/second.png");

        Assert.Equal(first.Id, firstReply.ConnectionId);
        Assert.Equal(second.Id, secondReply.ConnectionId);
        Assert.NotEqual(firstReply.DeliveryId, secondReply.DeliveryId);
        Assert.Equal(second.Id, secondImage.ConnectionId);
        Assert.All(
            (await outbox.ListAsync(second.ProjectId, second.Id)).Entries,
            row => Assert.Equal(second.Id, row.ConnectionId));
    }
    [Fact]
    public async Task Anchored_agent_reply_rejects_a_connection_without_the_conversation_mapping()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var mapped = await CreateConnectionAsync(database, time);
        var unmapped = await CreateConnectionAsync(database, time, mapped.ProjectId, "agent-2");
        await CreateThreadMappingAsync(database, time, mapped, "C-mapped-only", "1710000000.000028");
        var outbox = CreateStore(database, time);

        var reply = await outbox.EnqueueAgentReplyAsync(
            unmapped.ProjectId,
            "C-mapped-only",
            "1710000000.000028",
            "wrong owner",
            connectionId: unmapped.Id,
            replyDispatchRef: "unmapped:turn");

        Assert.False(reply.Accepted);
        Assert.Empty((await outbox.ListAsync(unmapped.ProjectId, unmapped.Id)).Entries);
        Assert.Empty((await outbox.ListAsync(mapped.ProjectId, mapped.Id)).Entries);
    }
    [Fact]
    public async Task Agent_reply_leaves_the_session_card_status_ref_for_liveness_finalization()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        await CreateDmMappingAsync(database, time, connection, "C-reply-statusref");
        var outbox = CreateStore(database, time);
        var projection = new SlackStatusProjection(outbox);
        var source = new SlackMessageIdentity(connection.WorkspaceTeamId, "C-reply-statusref", "1710000000.000030");
        var progressDispatchRef = "agent-session-followup:session-statusref:turn-1:progress";
        await projection.EnqueueWorkingAsync(connection.ProjectId, connection.Id, source, threadTs: null, progressDispatchRef);

        var reply = await outbox.EnqueueAgentReplyAsync(connection.ProjectId, "C-reply-statusref", null, "the answer");

        Assert.True(reply.Accepted);
        var rows = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries;
        var card = Assert.Single(rows, row => row.Kind == SlackOutboxKinds.ReplaceableProgress);
        Assert.Equal(SlackStatusProjection.DispatchRef(source, "status"),
            SlackDeliveryPayload.Parse(card.PayloadJson).StatusDispatchRef);
        var replyRow = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries
            .Single(row => row.Kind == SlackOutboxKinds.TerminalResult);
        Assert.Null(SlackDeliveryPayload.Parse(replyRow.PayloadJson).StatusDispatchRef);
        Assert.Null(SlackDeliveryPayload.Parse(replyRow.PayloadJson).ProgressDispatchRef);

        // Simulate the terminal handler's source differing from the ingress source
        // (e.g. delivery.MessageTs null -> synthetic ts). FinalizeLivenessAsync must
        // target the ORIGINAL message (from StatusDispatchRef), not the synthetic one —
        // otherwise the reaction mutation targets a non-existent message and stalls.
        var deliverySource = new SlackMessageIdentity(connection.WorkspaceTeamId, "C-reply-statusref", "terminal:synthetic");
        await projection.FinalizeLivenessAsync(
            connection.ProjectId, connection.Id, deliverySource, threadTs: null, "completed", progressDispatchRef);

        var finalized = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries;
        Assert.Contains(finalized, row => row.Kind == SlackOutboxKinds.ReactionMutation
            && row.DispatchRef == SlackStatusProjection.DispatchRef(source, "terminal-add"));
        Assert.DoesNotContain(finalized, row => row.Kind == SlackOutboxKinds.ReactionMutation
            && row.DispatchRef == SlackStatusProjection.DispatchRef(deliverySource, "terminal-add"));
    }
    [Fact]
    public async Task Agent_reply_without_an_active_conversation_is_not_accepted()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        var outbox = CreateStore(database, time);

        var reply = await outbox.EnqueueAgentReplyAsync(connection.ProjectId, "C-reply-unknown", null, "hello");

        Assert.False(reply.Accepted);
    }
    [Fact]
    public async Task Agent_reply_with_public_image_url_posts_an_image_block()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        await CreateDmMappingAsync(database, time, connection, "C-render-image");
        var outbox = CreateStore(database, time);
        var reply = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId, "C-render-image", null, "看图",
            connectionId: connection.Id, imageUrl: "https://example.com/chart.png");

        Assert.True(reply.Accepted);
        var row = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries.Single();
        Assert.EndsWith(":image", row.DispatchRef, StringComparison.Ordinal);
        var payload = SlackDeliveryPayload.Parse(row.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.PostMessage, payload.Operation);
        var blocks = JsonNode.Parse(payload.Blocks!.Value.GetRawText())!.AsArray();
        Assert.Equal(2, blocks.Count);
        Assert.Equal("section", blocks[0]!["type"]!.GetValue<string>());
        Assert.Equal("mrkdwn", blocks[0]!["text"]!["type"]!.GetValue<string>());
        Assert.Equal("看图", blocks[0]!["text"]!["text"]!.GetValue<string>());
        Assert.Equal("image", blocks[1]!["type"]!.GetValue<string>());
        Assert.Equal("https://example.com/chart.png", blocks[1]!["image_url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Agent_reply_with_image_url_and_no_text_posts_only_the_image()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        await CreateDmMappingAsync(database, time, connection, "C-render-image-only");
        var outbox = CreateStore(database, time);
        var reply = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId, "C-render-image-only", null, string.Empty,
            connectionId: connection.Id, imageUrl: "https://example.com/p.png");

        Assert.True(reply.Accepted);
        var row = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries.Single();
        var payload = SlackDeliveryPayload.Parse(row.PayloadJson);
        Assert.True(string.IsNullOrEmpty(payload.Text));
        var imageBlock = Assert.Single(JsonNode.Parse(payload.Blocks!.Value.GetRawText())!.AsArray());
        Assert.Equal("image", imageBlock!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Agent_reply_with_local_file_uploads_it_as_a_file_share()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        await CreateDmMappingAsync(database, time, connection, "D-render-file");
        var outbox = CreateStore(database, time);
        var base64 = Convert.ToBase64String("png-bytes"u8);
        var file = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId, "D-render-file", null, string.Empty,
            connectionId: connection.Id, fileName: "shot.png", fileContentBase64: base64);

        Assert.True(file.Accepted);
        var rows = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries;
        var fileRow = Assert.Single(rows);
        Assert.EndsWith(":file", fileRow.DispatchRef, StringComparison.Ordinal);
        var payload = SlackDeliveryPayload.Parse(fileRow.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.UploadFile, payload.Operation);
        Assert.Equal("shot.png", payload.FileName);
        Assert.Equal(base64, payload.FileContentBase64);
        Assert.True(string.IsNullOrEmpty(payload.Text));

        var text = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId, "D-render-file", null, "screenshot attached",
            connectionId: connection.Id);
        Assert.True(text.Accepted);
        rows = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries;
        Assert.Contains(rows, row => row.DispatchRef!.EndsWith(":file", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.DispatchRef!.EndsWith(":terminal", StringComparison.Ordinal));
    }

    private static SlackOutboxStore CreateStore(
        TestSqliteDatabase database,
        TimeProvider time,
        int capacity = 100) =>
        new(
            new TestDbContextFactory(database.Options),
            new NoopHealthBackpressurer(),
            time,
            Options.Create(new SlackProviderOptions { OutboxCapacityPerConnection = capacity }));

    private static async Task<AgentConnection> CreateConnectionAsync(
        TestSqliteDatabase database,
        TimeProvider time,
        string? projectId = null,
        string agentId = "agent-1")
    {
        projectId ??= $"project_{Guid.NewGuid():N}";
        var connection = new AgentConnection
        {
            Id = $"connection_{Guid.NewGuid():N}",
            ProjectId = projectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = "T123",
        };
        await using var db = database.CreateContext();
        if (!await db.Projects.AnyAsync(row => row.Id == projectId))
            db.Projects.Add(new ProjectRow
            {
                Id = projectId,
                Name = projectId,
                CreatedAt = time.GetUtcNow(),
                UpdatedAt = time.GetUtcNow(),
            });
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = connection.Id,
            ProjectId = projectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = connection.WorkspaceTeamId,
            AppId = "A123",
            BotUserId = "U123",
            BotName = "Mohist",
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            OwnerSlackUserId = "U_OWNER",
            CreatedAt = time.GetUtcNow(),
            UpdatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync();
        return connection;
    }

    private static async Task CreateDmMappingAsync(
        TestSqliteDatabase database,
        TimeProvider time,
        AgentConnection connection,
        string conversationId)
    {
        await using var db = database.CreateContext();
        db.SlackDmSessionMappings.Add(new SlackDmSessionMappingRow
        {
            Id = $"dm_{Guid.NewGuid():N}",
            ProjectId = connection.ProjectId,
            ConnectionId = connection.Id,
            WorkspaceTeamId = connection.WorkspaceTeamId,
            SlackUserId = "U_OWNER",
            DmConversationId = conversationId,
            CurrentSessionId = $"session_{Guid.NewGuid():N}",
            UpdatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }

    private static async Task CreateThreadMappingAsync(
        TestSqliteDatabase database,
        TimeProvider time,
        AgentConnection connection,
        string conversationId,
        string threadTs)
    {
        await using var db = database.CreateContext();
        db.SlackThreadSessionMappings.Add(new SlackThreadSessionMappingRow
        {
            Id = $"thread_{Guid.NewGuid():N}",
            ProjectId = connection.ProjectId,
            ConnectionId = connection.Id,
            WorkspaceTeamId = connection.WorkspaceTeamId,
            ConversationId = conversationId,
            ThreadTs = threadTs,
            SlackUserId = "U_OWNER",
            SessionId = $"session_{Guid.NewGuid():N}",
            RootMessageTs = threadTs,
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
