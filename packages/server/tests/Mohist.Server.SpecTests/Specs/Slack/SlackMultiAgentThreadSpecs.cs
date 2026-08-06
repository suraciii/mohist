using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Slack;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// Multi-agent thread behavior split off from
/// <see cref="SlackMultiAgentIngressSpecs"/>: thread launch / session sharing,
/// per-agent binding isolation, workspace attribution and threaded ambiguous
/// prompting. Shares the same fixture, seed helpers and cached connection
/// leases as the partial.
/// </summary>
public sealed partial class SlackMultiAgentIngressSpecs
{
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

    [Fact]
    public async Task Explicit_single_bot_in_multi_agent_thread_continues_only_that_bot()
    {
        var sharedProjectId = $"project_{Guid.NewGuid():N}";
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi-5", "U_BOT_A5", "A_BOT_A5", sharedProjectId);
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi-5", "U_BOT_B5", "A_BOT_B5", sharedProjectId);

        var firstA = await PostChannelAsync(connectionA, "C-multi-iso",
            messageTs: "1710000000.010500",
            threadTs: null,
            mentions: new[] { connectionA.BotUserId },
            text: $"<@{connectionA.BotUserId}> first task");
        var sessionAId = firstA.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrEmpty(sessionAId));

        var firstB = await PostChannelAsync(connectionB, "C-multi-iso",
            messageTs: "1710000000.010510",
            threadTs: "1710000000.010500",
            mentions: new[] { connectionB.BotUserId },
            text: $"<@{connectionB.BotUserId}> second task");
        var sessionBId = firstB.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrEmpty(sessionBId));

        var replyB = await PostChannelAsync(connectionB, "C-multi-iso",
            messageTs: "1710000000.010520",
            threadTs: "1710000000.010500",
            mentions: new[] { connectionB.BotUserId },
            text: $"<@{connectionB.BotUserId}> follow-up");
        Assert.Equal(sessionBId, replyB.GetProperty("sessionId").GetString());
        Assert.True(replyB.GetProperty("followup").GetBoolean());

        var replyA = await PostChannelAsync(connectionA, "C-multi-iso",
            messageTs: "1710000000.010530",
            threadTs: "1710000000.010500",
            mentions: new[] { connectionA.BotUserId },
            text: $"<@{connectionA.BotUserId}> follow-up");
        Assert.Equal(sessionAId, replyA.GetProperty("sessionId").GetString());
        Assert.True(replyA.GetProperty("followup").GetBoolean());
    }

    [Fact]
    public async Task First_second_agent_binding_is_isolated_from_existing_binding()
    {
        var sharedProjectId = $"project_{Guid.NewGuid():N}";
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi-6", "U_BOT_A6", "A_BOT_A6", sharedProjectId);
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi-6", "U_BOT_B6", "A_BOT_B6", sharedProjectId);

        var firstA = await PostChannelAsync(connectionA, "C-multi-bind",
            messageTs: "1710000000.010600",
            threadTs: null,
            mentions: new[] { connectionA.BotUserId },
            text: $"<@{connectionA.BotUserId}> first");
        var sessionAId = firstA.GetProperty("sessionId").GetString();

        var secondB = await PostChannelAsync(connectionB, "C-multi-bind",
            messageTs: "1710000000.010610",
            threadTs: "1710000000.010600",
            mentions: new[] { connectionB.BotUserId },
            text: $"<@{connectionB.BotUserId}> second");
        var sessionBId = secondB.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrEmpty(sessionBId));
        Assert.NotEqual(sessionAId, sessionBId);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        var bindings = await threadMapping.ListBindingsAsync(
            connectionA.ProjectId, connectionA.WorkspaceTeamId,
            "C-multi-bind", "1710000000.010600");
        Assert.Equal(2, bindings.Count);

        var firstASessionId = (await threadMapping.GetSessionIdAsync(
            connectionA.ProjectId, connectionA.WorkspaceTeamId, connectionA.Id,
            "C-multi-bind", "1710000000.010600"))!;
        var secondBSessionId = (await threadMapping.GetSessionIdAsync(
            connectionB.ProjectId, connectionB.WorkspaceTeamId, connectionB.Id,
            "C-multi-bind", "1710000000.010600"))!;
        Assert.Equal(sessionAId, firstASessionId);
        Assert.Equal(sessionBId, secondBSessionId);
    }

    [Fact]
    public async Task Two_workspaces_with_same_channel_thread_do_not_share_attribution()
    {
        var teamX = "T-workspace-X";
        var teamY = "T-workspace-Y";
        var connectionX = await CreateConnectionAsync("agent-X", teamX, "U_BOT_X", "A_BOT_X");
        var connectionY = await CreateConnectionAsync("agent-Y", teamY, "U_BOT_Y", "A_BOT_Y");

        var firstX = await PostChannelAsync(connectionX, "C-shared",
            messageTs: "1710000000.010700",
            threadTs: null,
            mentions: new[] { connectionX.BotUserId },
            text: $"<@{connectionX.BotUserId}> workspace X task");
        var sessionXId = firstX.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrEmpty(sessionXId));

        var replyX = await PostChannelAsync(connectionX, "C-shared",
            messageTs: "1710000000.010710",
            threadTs: "1710000000.010700",
            mentions: Array.Empty<string>(),
            text: "continue X thread");
        Assert.Equal(sessionXId, replyX.GetProperty("sessionId").GetString());
        Assert.True(replyX.GetProperty("followup").GetBoolean());

        var replyY = await PostChannelAsync(connectionY, "C-shared",
            messageTs: "1710000000.010710",
            threadTs: "1710000000.010700",
            mentions: Array.Empty<string>(),
            text: "no attribution to X thread");
        Assert.Equal("ignored", replyY.GetProperty("kind").GetString());

        var firstY = await PostChannelAsync(connectionY, "C-shared",
            messageTs: "1710000000.010700",
            threadTs: null,
            mentions: new[] { connectionY.BotUserId },
            text: $"<@{connectionY.BotUserId}> workspace Y task");
        var sessionYId = firstY.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrEmpty(sessionYId));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var sessions = await db.AgentSessions
            .Where(row => row.LabelConnectionId == connectionX.Id || row.LabelConnectionId == connectionY.Id)
            .ToListAsync();
        Assert.Equal(2, sessions.Count);
        var xRows = sessions.Where(row => row.LabelConnectionId == connectionX.Id).ToList();
        var yRows = sessions.Where(row => row.LabelConnectionId == connectionY.Id).ToList();
        Assert.Single(xRows);
        Assert.Single(yRows);

        var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        var bindingsX = await threadMapping.ListBindingsAsync(
            connectionX.ProjectId, connectionX.WorkspaceTeamId, "C-shared", "1710000000.010700");
        var bindingsY = await threadMapping.ListBindingsAsync(
            connectionY.ProjectId, connectionY.WorkspaceTeamId, "C-shared", "1710000000.010700");
        Assert.Single(bindingsX);
        Assert.Single(bindingsY);
        Assert.Equal(sessionXId, bindingsX[0].SessionId);
        Assert.Equal(sessionYId, bindingsY[0].SessionId);
    }

    [Fact]
    public async Task Ambiguous_prompt_threaded_reply_is_prompted_in_thread()
    {
        var sharedProjectId = $"project_{Guid.NewGuid():N}";
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi-7", "U_BOT_A7", "A_BOT_A7", sharedProjectId);
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi-7", "U_BOT_B7", "A_BOT_B7", sharedProjectId);

        var firstA = await PostChannelAsync(connectionA, "C-thread-prompt",
            messageTs: "1710000000.010800",
            threadTs: null,
            mentions: new[] { connectionA.BotUserId },
            text: $"<@{connectionA.BotUserId}> first");
        Assert.False(string.IsNullOrEmpty(firstA.GetProperty("sessionId").GetString()));

        var firstB = await PostChannelAsync(connectionB, "C-thread-prompt",
            messageTs: "1710000000.010810",
            threadTs: "1710000000.010800",
            mentions: new[] { connectionB.BotUserId },
            text: $"<@{connectionB.BotUserId}> second");
        Assert.False(string.IsNullOrEmpty(firstB.GetProperty("sessionId").GetString()));

        var ambiguous = await PostChannelAsync(connectionA, "C-thread-prompt",
            messageTs: "1710000000.010820",
            threadTs: "1710000000.010800",
            mentions: Array.Empty<string>(),
            text: "human chat");
        Assert.Equal("ambiguous", ambiguous.GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var promptRows = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connectionA.Id
                && row.ConversationId == "C-thread-prompt"
                && row.ThreadTs == "1710000000.010800"
                && row.Kind == SlackOutboxKinds.UserAction)
            .ToListAsync();
        Assert.Contains(promptRows, row => row.PayloadJson.Contains("Multiple Agents", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unrelated_connection_does_not_claim_ambiguous_prompt()
    {
        var sharedProjectId = $"project_{Guid.NewGuid():N}";
        var connectionA = await CreateConnectionAsync("agent-A", "T-unrelated", "U_OWNER_A", "A_UNRELATED_A", sharedProjectId);
        var connectionB = await CreateConnectionAsync("agent-B", "T-unrelated", "U_OWNER_B", "A_UNRELATED_B", sharedProjectId);
        var connectionC = await CreateConnectionAsync("agent-C", "T-unrelated", "U_OWNER_C", "A_UNRELATED_C", sharedProjectId);

        var root = await PostChannelAsync(
            connectionC,
            "C-unrelated-root",
            "1710000000.010900",
            null,
            new[] { connectionA.BotUserId, connectionB.BotUserId },
            $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> choose");
        Assert.Equal("ignored", root.GetProperty("kind").GetString());

        await PostChannelAsync(
            connectionA,
            "C-unrelated-thread",
            "1710000000.010910",
            null,
            new[] { connectionA.BotUserId },
            $"<@{connectionA.BotUserId}> first");
        await PostChannelAsync(
            connectionB,
            "C-unrelated-thread",
            "1710000000.010920",
            "1710000000.010910",
            new[] { connectionB.BotUserId },
            $"<@{connectionB.BotUserId}> second");

        var reply = await PostChannelAsync(
            connectionC,
            "C-unrelated-thread",
            "1710000000.010930",
            "1710000000.010910",
            Array.Empty<string>(),
            "human discussion");
        Assert.Equal("ignored", reply.GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connectionC.Id
                && (row.ConversationId == "C-unrelated-root"
                    || row.ConversationId == "C-unrelated-thread"))
            .ToListAsync());
    }
}
