using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Slack;
using Xunit;

namespace Mohist.Server.Tests.Slack;

/// <summary>
/// Multi-agent thread composition proofs split off from
/// <see cref="SlackMultiAgentIngressSpecs"/>: ambiguous replies and concurrent
/// agent binding. Shares the same fixture, seed helpers and cached connection
/// leases as the partial.
/// </summary>
public sealed partial class SlackMultiAgentIngressSpecs
{
    [Fact]
    public async Task Explicit_single_bot_in_multi_agent_thread_continues_only_that_bot()
    {
        var sharedProjectId = $"project_{Guid.NewGuid():N}";
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi-explicit", "U_BOT_A", "A_BOT_A", sharedProjectId);
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi-explicit", "U_BOT_B", "A_BOT_B", sharedProjectId);

        var firstA = await PostChannelAsync(connectionA, "C-multi-explicit",
            messageTs: "1710000000.010500",
            threadTs: null,
            mentions: new[] { connectionA.BotUserId },
            text: $"<@{connectionA.BotUserId}> first task");
        var sessionAId = firstA.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrEmpty(sessionAId));

        var firstB = await PostChannelAsync(connectionB, "C-multi-explicit",
            messageTs: "1710000000.010510",
            threadTs: "1710000000.010500",
            mentions: new[] { connectionB.BotUserId },
            text: $"<@{connectionB.BotUserId}> second task");
        var sessionBId = firstB.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrEmpty(sessionBId));

        var replyB = await PostChannelAsync(connectionB, "C-multi-explicit",
            messageTs: "1710000000.010520",
            threadTs: "1710000000.010500",
            mentions: new[] { connectionB.BotUserId },
            text: $"<@{connectionB.BotUserId}> follow-up");
        Assert.Equal(sessionBId, replyB.GetProperty("sessionId").GetString());
        Assert.NotEqual(sessionAId, replyB.GetProperty("sessionId").GetString());
        Assert.True(replyB.GetProperty("followup").GetBoolean());
    }

    [Fact]
    public async Task Multi_agent_thread_no_mention_starts_no_work_and_prompts_once()
    {
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi-4", "U_BOT_A4", "A_BOT_A4");
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi-4", "U_BOT_B4", "A_BOT_B4");

        var firstA = await PostChannelAsync(connectionA, "C-multi-thread",
            messageTs: "1710000000.010400",
            threadTs: null,
            mentions: new[] { connectionA.BotUserId },
            text: $"<@{connectionA.BotUserId}> first task");
        Assert.False(string.IsNullOrEmpty(firstA.GetProperty("sessionId").GetString()));

        var firstB = await PostChannelAsync(connectionB, "C-multi-thread",
            messageTs: "1710000000.010410",
            threadTs: "1710000000.010400",
            mentions: new[] { connectionB.BotUserId },
            text: $"<@{connectionB.BotUserId}> second agent task");
        Assert.False(string.IsNullOrEmpty(firstB.GetProperty("sessionId").GetString()));
        Assert.NotEqual(firstA.GetProperty("sessionId").GetString(), firstB.GetProperty("sessionId").GetString());

        var reply = await PostChannelAsync(connectionA, "C-multi-thread",
            messageTs: "1710000000.010420",
            threadTs: "1710000000.010400",
            mentions: Array.Empty<string>(),
            text: "human discussion");
        var otherReply = await PostChannelAsync(connectionB, "C-multi-thread",
            messageTs: "1710000000.010420",
            threadTs: "1710000000.010400",
            mentions: Array.Empty<string>(),
            text: "human discussion",
            senderSlackUserId: connectionA.OwnerSlackUserId);
        Assert.Equal("ambiguous", reply.GetProperty("kind").GetString());
        Assert.Equal("ignored", otherReply.GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var sessions = await db.AgentSessions
            .Where(row => row.LabelConnectionId == connectionA.Id || row.LabelConnectionId == connectionB.Id)
            .ToListAsync();
        Assert.Equal(2, sessions.Count);
        var inboxRowsForReply = await db.SlackProviderInboxRows
            .Where(row => (row.ConnectionId == connectionA.Id || row.ConnectionId == connectionB.Id)
                && row.ConversationId == "C-multi-thread"
                && row.SlackMessageIdentity.EndsWith("1710000000.010420"))
            .ToListAsync();
        Assert.Empty(inboxRowsForReply);
    }

    [Fact]
    public async Task Concurrent_mentions_of_new_agent_share_one_session()
    {
        var sharedProjectId = $"project_{Guid.NewGuid():N}";
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi-launch", "U_OWNER_A", "A_LAUNCH_A", sharedProjectId);
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi-launch", "U_OWNER_B", "A_LAUNCH_B", sharedProjectId);
        var conversationId = "C-multi-launch";
        var rootTs = "1710000000.010600";
        await PostChannelAsync(
            connectionA,
            conversationId,
            rootTs,
            null,
            new[] { connectionA.BotUserId },
            $"<@{connectionA.BotUserId}> establish thread");

        var messages = new[]
        {
            (MessageTs: "1710000000.010610", Text: $"<@{connectionB.BotUserId}> first B task"),
            (MessageTs: "1710000000.010620", Text: $"<@{connectionB.BotUserId}> second B task"),
        };
        var attempts = await Task.WhenAll(messages.Select(message => PostChannelAttemptAsync(
            connectionB,
            conversationId,
            message.MessageTs,
            rootTs,
            new[] { connectionB.BotUserId },
            message.Text,
            connectionB.OwnerSlackUserId)));

        for (var index = 0; index < attempts.Length; index++)
        {
            if (attempts[index].Status == HttpStatusCode.Conflict)
                await PostChannelAsync(
                    connectionB,
                    conversationId,
                    messages[index].MessageTs,
                    rootTs,
                    new[] { connectionB.BotUserId },
                    messages[index].Text,
                    connectionB.OwnerSlackUserId);
            else
                Assert.Equal(HttpStatusCode.OK, attempts[index].Status);
        }

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Single(await db.AgentSessions
            .Where(row => row.LabelConnectionId == connectionB.Id)
            .ToListAsync());
        Assert.Equal(2, await db.AgentJobs.CountAsync(row => row.ProjectId == sharedProjectId));
        var mapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        var bindings = await mapping.ListBindingsAsync(
            sharedProjectId, connectionB.WorkspaceTeamId, conversationId, rootTs);
        Assert.Equal(2, bindings.Count);
        Assert.Single(bindings, binding => binding.ConnectionId == connectionB.Id);
    }

}
