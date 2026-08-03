using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("MohistIntegration")]
public sealed class SlackMultiAgentIngressSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackMultiAgentIngressSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Multi_bot_mention_starts_no_work_and_prompts_once()
    {
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi", "U_BOT_A", "A_BOT_A");
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi", "U_BOT_B", "A_BOT_B");

        var body = new
        {
            isDirectMessage = false,
            teamId = connectionA.WorkspaceTeamId,
            conversationId = "C-multi-bot",
            messageTs = "1710000000.010100",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connectionA.BotUserId, connectionB.BotUserId },
            senderSlackUserId = connectionA.OwnerSlackUserId,
            senderKind = "human",
            text = $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> who answers?",
        };

        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connectionA), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ambiguous", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.AgentSessions
            .Where(row => row.LabelConnectionId == connectionA.Id || row.LabelConnectionId == connectionB.Id)
            .ToListAsync());
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => (row.ConnectionId == connectionA.Id || row.ConnectionId == connectionB.Id)
                && row.ConversationId == "C-multi-bot")
            .ToListAsync());
        Assert.Empty(await db.SlackThreadSessionMappings
            .Where(row => row.ConversationId == "C-multi-bot")
            .ToListAsync());
    }

    [Fact]
    public async Task Ambiguous_message_is_not_rejected_by_a_non_claiming_owner()
    {
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi-owner-routing", "U_OWNER_A", "A_OWNER_A");
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi-owner-routing", "U_OWNER_B", "A_OWNER_B");
        var messageTs = "1710000000.010150";
        var text = $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> choose";

        var prompt = await PostChannelAsync(
            connectionA,
            "C-multi-owner-routing",
            messageTs,
            null,
            new[] { connectionA.BotUserId, connectionB.BotUserId },
            text,
            connectionA.OwnerSlackUserId);
        var otherDelivery = await PostChannelAsync(
            connectionB,
            "C-multi-owner-routing",
            messageTs,
            null,
            new[] { connectionA.BotUserId, connectionB.BotUserId },
            text,
            connectionA.OwnerSlackUserId);

        Assert.Equal("ambiguous", prompt.GetProperty("kind").GetString());
        Assert.Equal("ignored", otherDelivery.GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Single(await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connectionA.Id
                && row.ConversationId == "C-multi-owner-routing")
            .ToListAsync());
        Assert.Empty(await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connectionB.Id
                && row.ConversationId == "C-multi-owner-routing")
            .ToListAsync());
    }

    [Fact]
    public async Task Two_connections_mentioning_same_multi_bot_prompt_once()
    {
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi-2", "U_BOT_A2", "A_BOT_A2");
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi-2", "U_BOT_B2", "A_BOT_B2");

        var bodyA = new
        {
            isDirectMessage = false,
            teamId = connectionA.WorkspaceTeamId,
            conversationId = "C-multi-bot-2",
            messageTs = "1710000000.010200",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connectionA.BotUserId, connectionB.BotUserId },
            senderSlackUserId = connectionA.OwnerSlackUserId,
            senderKind = "human",
            text = $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> task",
        };
        using var responseA = await _fixture.Client.PostAsJsonAsync(IngressPath(connectionA), bodyA);
        responseA.EnsureSuccessStatusCode();
        using var docA = JsonDocument.Parse(await responseA.Content.ReadAsStringAsync());
        Assert.Equal("ambiguous", docA.RootElement.GetProperty("data").GetProperty("kind").GetString());

        var bodyB = new
        {
            isDirectMessage = false,
            teamId = connectionB.WorkspaceTeamId,
            conversationId = "C-multi-bot-2",
            messageTs = "1710000000.010200",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connectionA.BotUserId, connectionB.BotUserId },
            senderSlackUserId = connectionB.OwnerSlackUserId,
            senderKind = "human",
            text = $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> task",
        };
        using var responseB = await _fixture.Client.PostAsJsonAsync(IngressPath(connectionB), bodyB);
        responseB.EnsureSuccessStatusCode();
        using var docB = JsonDocument.Parse(await responseB.Content.ReadAsStringAsync());
        Assert.Equal("ambiguous", docB.RootElement.GetProperty("data").GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var promptRows = await db.SlackOutboxRows
            .Where(row => (row.ConnectionId == connectionA.Id || row.ConnectionId == connectionB.Id)
                && row.ConversationId == "C-multi-bot-2"
                && row.Kind == SlackOutboxKinds.UserAction)
            .ToListAsync();
        var promptMessages = promptRows
            .Select(row => JsonDocument.Parse(row.PayloadJson).RootElement.GetProperty("text").GetString() ?? string.Empty)
            .ToList();
        Assert.Single(promptRows);
        Assert.Contains(promptMessages, text => text.Contains("Multiple Agents", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Redelivered_ambiguous_message_does_not_repeat_prompt()
    {
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi-3", "U_BOT_A3", "A_BOT_A3");
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi-3", "U_BOT_B3", "A_BOT_B3");

        var body = new
        {
            isDirectMessage = false,
            teamId = connectionA.WorkspaceTeamId,
            conversationId = "C-multi-bot-3",
            messageTs = "1710000000.010300",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connectionA.BotUserId, connectionB.BotUserId },
            senderSlackUserId = connectionA.OwnerSlackUserId,
            senderKind = "human",
            text = $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> task",
        };
        for (var i = 0; i < 3; i++)
        {
            using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connectionA), body);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("ambiguous", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());
        }

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var promptRows = await db.SlackOutboxRows
            .Where(row => (row.ConnectionId == connectionA.Id || row.ConnectionId == connectionB.Id)
                && row.ConversationId == "C-multi-bot-3"
                && row.Kind == SlackOutboxKinds.UserAction)
            .ToListAsync();
        Assert.Single(promptRows);
    }

    [Fact]
    public async Task Ambiguous_prompt_retries_after_claim_without_delivery()
    {
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi-retry", "U_OWNER", "A_BOT_RETRY_A");
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi-retry", "U_OWNER", "A_BOT_RETRY_B");
        const string conversationId = "C-multi-retry";
        const string messageTs = "1710000000.010350";

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var prompts = scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
            await prompts.TryClaimAsync(
                connectionA.ProjectId, connectionA.WorkspaceTeamId, conversationId, messageTs,
                null, connectionA.Id, new[] { connectionA.Id, connectionB.Id });
        }

        var replay = await PostChannelAsync(connectionA, conversationId,
            messageTs, null,
            new[] { connectionA.BotUserId, connectionB.BotUserId },
            $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> choose");
        Assert.Equal("ambiguous", replay.GetProperty("kind").GetString());

        await using var verify = _fixture.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        var promptRows = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connectionA.Id
                && row.DispatchRef == SlackAmbiguousPromptStore.PromptDispatchRef(
                    connectionA.WorkspaceTeamId, conversationId, messageTs))
            .ToListAsync();
        Assert.Single(promptRows);
    }

    [Fact]
    public async Task Non_owner_ambiguous_message_is_rejected_without_prompt()
    {
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi-owner", "U_OWNER", "A_BOT_OWNER_A");
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi-owner", "U_OWNER_B", "A_BOT_OWNER_B");

        var body = new
        {
            isDirectMessage = false,
            teamId = connectionA.WorkspaceTeamId,
            conversationId = "C-multi-owner",
            messageTs = "1710000000.010360",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connectionA.BotUserId, connectionB.BotUserId },
            senderSlackUserId = "U_OTHER",
            senderKind = "human",
            text = $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> choose",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connectionA), body);

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("rejected", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());
        Assert.Contains("owner", doc.RootElement.GetProperty("data").GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);

        using var otherResponse = await _fixture.Client.PostAsJsonAsync(IngressPath(connectionB), body);
        otherResponse.EnsureSuccessStatusCode();
        using var otherDoc = JsonDocument.Parse(await otherResponse.Content.ReadAsStringAsync());
        Assert.Equal("ignored", otherDoc.RootElement.GetProperty("data").GetProperty("kind").GetString());
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

    private async Task<JsonElement> PostChannelAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string? threadTs,
        string[] mentions,
        string text,
        string? senderSlackUserId = null)
    {
        var result = await PostChannelAttemptAsync(
            connection,
            conversationId,
            messageTs,
            threadTs,
            mentions,
            text,
            senderSlackUserId);
        Assert.Equal(HttpStatusCode.OK, result.Status);
        return result.Data!.Value;
    }

    private async Task<(HttpStatusCode Status, JsonElement? Data)> PostChannelAttemptAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string? threadTs,
        string[] mentions,
        string text,
        string? senderSlackUserId = null)
    {
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            threadTs,
            mentionedUserIds = mentions,
            senderSlackUserId = senderSlackUserId ?? connection.OwnerSlackUserId ?? "U_OWNER",
            senderKind = "human",
            text,
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        if (!response.IsSuccessStatusCode)
            return (response.StatusCode, null);
        var raw = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(raw))
            return (response.StatusCode, null);
        var document = JsonDocument.Parse(raw);
        return (response.StatusCode, document.RootElement.GetProperty("data").Clone());
    }

    private async Task<AgentConnection> CreateConnectionAsync(
        string agentNameSuffix,
        string workspaceTeamId,
        string ownerSlackUserId,
        string appId,
        string? projectId = null)
    {
        var id = $"connection_{Guid.NewGuid():N}";
        var resolvedProjectId = projectId ?? $"project_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();
        _fixture.Slack.UsersInfo = new(true, null,
            new(ownerSlackUserId, workspaceTeamId, false, false, false, false, false));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var existingProject = await db.Projects
            .FirstOrDefaultAsync(p => p.Id == resolvedProjectId);
        if (existingProject is null)
        {
            db.Projects.Add(new ProjectRow
            {
                Id = resolvedProjectId,
                Name = resolvedProjectId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        var botUserId = $"U{agentNameSuffix.GetHashCode():X}".PadRight(8, '0').Substring(0, 8);
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = resolvedProjectId,
            Name = $"Mohist Agent {agentNameSuffix}",
            Status = AgentStatus.Active,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = resolvedProjectId,
                Name = $"Mohist Agent {agentNameSuffix}",
                Status = AgentStatus.Active,
                AgentConfig = JsonSerializer.SerializeToElement(new { model = "openai/gpt-4o", runtime = "opencode" }),
            }, JSON.Options),
        });
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = id,
            ProjectId = resolvedProjectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = workspaceTeamId,
            AppId = appId,
            BotUserId = botUserId,
            BotName = $"Mohist {agentNameSuffix}".Trim(),
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            OwnerSlackUserId = ownerSlackUserId,
            LastHeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await secrets.StoreAsync(new SecretStoreAddress(resolvedProjectId, id, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp"));
        await secrets.StoreAsync(new SecretStoreAddress(resolvedProjectId, id, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb"));
        return new AgentConnection
        {
            Id = id,
            ProjectId = resolvedProjectId,
            WorkspaceTeamId = workspaceTeamId,
            BotUserId = botUserId,
            OwnerSlackUserId = ownerSlackUserId,
        };
    }

    private static string IngressPath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress";
}
