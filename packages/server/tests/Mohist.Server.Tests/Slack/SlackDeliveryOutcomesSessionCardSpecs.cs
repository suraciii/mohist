using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Slack;

public sealed partial class SlackDeliveryHandlerSpecs
{
    [Fact]
    public async Task Agent_reply_is_accepted_when_the_session_card_is_delivered()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        await CreateDmMappingAsync(database, time, connection, "C-reply-delivered-card");
        var outbox = CreateStore(database, time);
        var projection = new SlackStatusProjection(outbox);
        var source = new SlackMessageIdentity(
            connection.WorkspaceTeamId, "C-reply-delivered-card", "1710000000.000021");
        var card = await projection.EnqueueWorkingAsync(
            connection.ProjectId, connection.Id, source, threadTs: null, sessionId: "session-delivered-card");
        var providerIdentity = new SlackProviderMessageIdentity(
            source.ConversationId, "1710000000.000022");
        await outbox.MarkDeliveredAsync(connection.ProjectId, card.Id, providerIdentity);

        var reply = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            source.ConversationId,
            source.MessageTs,
            "delivered card answer",
            connectionId: connection.Id,
            replyDispatchRef: "agent-session-delivered-card:turn-1");

        Assert.True(reply.Accepted);
        Assert.NotEqual(card.Id, reply.DeliveryId);
        var rows = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries;
        var persistedCard = Assert.Single(rows, row => row.Id == card.Id);
        Assert.Equal(SlackOutboxKinds.ReplaceableProgress, persistedCard.Kind);
        Assert.Equal(SlackOutboxStates.Delivered, persistedCard.State);
        Assert.Equal(
            providerIdentity,
            SlackDeliveryPayload.Parse(persistedCard.PayloadJson).ProviderMessageIdentity);
        Assert.Single(rows, row => row.Id == reply.DeliveryId
            && row.Kind == SlackOutboxKinds.TerminalResult);
    }

    [Fact]
    public async Task Agent_reply_is_accepted_without_overwriting_a_claimed_session_card()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        await CreateDmMappingAsync(database, time, connection, "C-reply-claimed-card");
        var outbox = CreateStore(database, time);
        var card = await EnqueueSessionCardAsync(
            outbox, connection, "C-reply-claimed-card", "claimed");
        var claimed = await outbox.ClaimAsync(connection.ProjectId, connection.Id, "adapter-card");
        Assert.Equal(card.Id, claimed?.Id);

        var reply = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "C-reply-claimed-card",
            "1710000000.000023",
            "claimed card answer",
            connectionId: connection.Id,
            replyDispatchRef: "agent-session-claimed-card:turn-1");

        Assert.True(reply.Accepted);
        Assert.NotEqual(card.Id, reply.DeliveryId);
        var rows = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries;
        var claimedCard = Assert.Single(rows, row => row.Id == card.Id);
        Assert.Equal(SlackOutboxKinds.ReplaceableProgress, claimedCard.Kind);
        Assert.Equal(SlackOutboxStates.Claimed, claimedCard.State);
        Assert.Equal("adapter-card", claimedCard.ClaimedByAdapterId);
        Assert.Single(rows, row => row.Id == reply.DeliveryId
            && row.Kind == SlackOutboxKinds.TerminalResult);
    }

    [Fact]
    public async Task Agent_reply_is_accepted_without_overwriting_an_uncertain_session_card()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var connection = await CreateConnectionAsync(database, time);
        await CreateDmMappingAsync(database, time, connection, "C-reply-uncertain-card");
        var outbox = CreateStore(database, time);
        var card = await EnqueueSessionCardAsync(
            outbox, connection, "C-reply-uncertain-card", "uncertain");
        var claimed = await outbox.ClaimAsync(connection.ProjectId, connection.Id, "adapter-card");
        Assert.Equal(card.Id, claimed?.Id);
        await outbox.MarkDeliveryUncertainAsync(
            connection.ProjectId, card.Id, "provider response lost", "adapter-card");

        var reply = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "C-reply-uncertain-card",
            "1710000000.000024",
            "uncertain card answer",
            connectionId: connection.Id,
            replyDispatchRef: "agent-session-uncertain-card:turn-1");

        Assert.True(reply.Accepted);
        Assert.NotEqual(card.Id, reply.DeliveryId);
        var rows = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries;
        var uncertainCard = Assert.Single(rows, row => row.Id == card.Id);
        Assert.Equal(SlackOutboxKinds.ReplaceableProgress, uncertainCard.Kind);
        Assert.Equal(SlackOutboxStates.DeliveryUncertain, uncertainCard.State);
        Assert.Null(SlackDeliveryPayload.Parse(uncertainCard.PayloadJson).ProviderMessageIdentity);
        Assert.Single(rows, row => row.Id == reply.DeliveryId
            && row.Kind == SlackOutboxKinds.TerminalResult);
    }

    private static Task<SlackOutboxEnqueueResult> EnqueueSessionCardAsync(
        SlackOutboxStore outbox,
        AgentConnection connection,
        string conversationId,
        string suffix) =>
        outbox.EnqueueAsync(new SlackOutboxDraft(
            connection.ProjectId,
            connection.Id,
            connection.WorkspaceTeamId,
            conversationId,
            SlackOutboxKinds.ReplaceableProgress,
            $"session-card:{suffix}",
            JsonSerializer.Serialize(new SlackDeliveryPayload(
                SlackDeliveryOperations.PostMessage,
                "Agent session.",
                ClientMessageId: $"session-card:{suffix}",
                StatusDispatchRef: $"session-card-status:{suffix}"))));
}
