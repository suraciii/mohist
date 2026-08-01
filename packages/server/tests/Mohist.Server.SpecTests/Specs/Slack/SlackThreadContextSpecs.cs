using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("MohistIntegration")]
public sealed class SlackThreadContextSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackThreadContextSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task FirstMentionInExistingThread_ImportsThreadHistory_AsStartupContext()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-thread-import";
        const string rootTs = "1710000000.000100";
        const string mentionTs = "1710000000.000200";
        EnqueueReplies(new SlackConversationsRepliesPage(
            true,
            null,
            new[]
            {
                Message("1710000000.000110", "U_ALICE", "should we ship?"),
                Message("1710000000.000120", "U_BOB", "yes, after code review"),
            },
            null));

        var result = await PostChannelMentionAsync(
            connection,
            conversationId,
            mentionTs,
            threadTs: rootTs,
            text: "<@U123> summarize the decision");

        Assert.Equal("accepted", result.GetProperty("kind").GetString());
        var sessionId = result.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        var snapshot = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId!).GetInitialLaunchAsync();
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.Input);
        Assert.NotNull(snapshot.Input.StartupContext);
        Assert.Equal("summarize the decision", snapshot.Input.Text);
        Assert.Equal("slack-thread-history", snapshot.Input.StartupContext!.Provenance.Source);
        Assert.False(snapshot.Input.StartupContext.Provenance.Truncated);
        Assert.Contains("U_ALICE: should we ship?", snapshot.Input.StartupContext.Text, StringComparison.Ordinal);
        Assert.Contains("U_BOB: yes, after code review", snapshot.Input.StartupContext.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RootMention_BrandNewThread_ImportsNoStartupContext()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-root-no-import";
        const string mentionTs = "1710000000.000300";
        EnqueueReplies(new SlackConversationsRepliesPage(true, null, [], null));

        var result = await PostChannelMentionAsync(
            connection,
            conversationId,
            mentionTs,
            threadTs: null,
            text: "<@U123> first task");

        Assert.Equal("accepted", result.GetProperty("kind").GetString());
        var sessionId = result.GetProperty("sessionId").GetString();
        var snapshot = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId!).GetInitialLaunchAsync();
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.Input);
        Assert.Null(snapshot.Input.StartupContext);
    }

    [Fact]
    public async Task OverBudget_TruncatesOldestFirst_AndDualMarkedInReplyAndInput()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-thread-truncate";
        const string rootTs = "1710000000.000400";
        const string mentionTs = "1710000000.000450";
        var longText = new string('a', 2000);
        var messages = new List<SlackConversationMessage>
        {
            Message("1710000000.000410", "U_USER_0", longText),
            Message("1710000000.000420", "U_USER_1", longText),
            Message("1710000000.000430", "U_USER_2", longText),
            Message("1710000000.000440", "U_USER_3", longText),
            Message("1710000000.000445", "U_USER_4", longText),
            Message("1710000000.000460", "U_NEWEST", "newest message"),
        };
        EnqueueReplies(new SlackConversationsRepliesPage(true, null, messages, null));

        var result = await PostChannelMentionAsync(
            connection,
            conversationId,
            mentionTs,
            threadTs: rootTs,
            text: "<@U123> summarize");

        Assert.Equal("accepted", result.GetProperty("kind").GetString());
        var sessionId = result.GetProperty("sessionId").GetString();
        var snapshot = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId!).GetInitialLaunchAsync();
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.Input!.StartupContext);
        Assert.True(snapshot.Input.StartupContext!.Provenance.Truncated);
        Assert.Contains("oldest messages omitted", snapshot.Input.StartupContext.Provenance.TruncationMarker, StringComparison.Ordinal);
        Assert.True(snapshot.Input.StartupContext.Provenance.OmittedOldestMessageCount > 0);
        Assert.Contains("newest message", snapshot.Input.StartupContext.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("U_USER_0", snapshot.Input.StartupContext.Text, StringComparison.Ordinal);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var replies = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == conversationId
                && row.ThreadTs == rootTs)
            .Select(row => new { row.Kind, row.PayloadJson, row.CreatedAt })
            .ToListAsync();
        Assert.NotEmpty(replies);
        var ack = replies.Where(r => r.Kind == SlackOutboxKinds.UserAction).ToList();
        Assert.NotEmpty(ack);
        var latest = ack.Last();
        Assert.Contains("oldest messages", latest.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("background", latest.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SlackRejection_RefusesLaunch_ReleasesReservation_AndNoAgentJobCreated()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-thread-refuse";
        const string rootTs = "1710000000.000500";
        const string mentionTs = "1710000000.000550";
        _fixture.Slack.ConversationsRepliesError = new SlackConversationsRepliesPage(false, "not_in_channel", null, null);

        var result = await PostChannelMentionAsync(
            connection,
            conversationId,
            mentionTs,
            threadTs: rootTs,
            text: "<@U123> summarize");

        Assert.Equal("rejected", result.GetProperty("kind").GetString());
        Assert.Contains("re-mention", result.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.AgentSessions
            .Where(row => row.LabelConnectionId == connection.Id
                && row.LabelSlackConversationId == conversationId
                && row.LabelSlackThreadTs == rootTs)
            .ToListAsync());
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == conversationId
                && row.SlackMessageIdentity.EndsWith(mentionTs))
            .ToListAsync());
        Assert.Empty(await db.SlackThreadLaunchReservations
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == conversationId
                && row.ThreadTs == rootTs)
            .ToListAsync());
    }

    [Fact]
    public async Task RateLimitedError_RefusesLaunch_NoAgentJob()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-thread-ratelimit";
        const string rootTs = "1710000000.000600";
        const string mentionTs = "1710000000.000650";
        _fixture.Slack.ConversationsRepliesError = new SlackConversationsRepliesPage(false, "ratelimited", null, null);

        var result = await PostChannelMentionAsync(
            connection,
            conversationId,
            mentionTs,
            threadTs: rootTs,
            text: "<@U123> summarize");

        Assert.Equal("rejected", result.GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackThreadLaunchReservations
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == conversationId
                && row.ThreadTs == rootTs)
            .ToListAsync());
    }

    [Fact]
    public async Task ReadFailureThenReMention_ReLaunchesAfterReservationReleased()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-thread-relaunch";
        const string rootTs = "1710000000.000700";
        const string firstMentionTs = "1710000000.000750";
        const string secondMentionTs = "1710000000.000760";
        _fixture.Slack.ConversationsRepliesError = new SlackConversationsRepliesPage(false, "not_in_channel", null, null);

        var first = await PostChannelMentionAsync(
            connection,
            conversationId,
            firstMentionTs,
            threadTs: rootTs,
            text: "<@U123> first attempt");
        Assert.Equal("rejected", first.GetProperty("kind").GetString());

        _fixture.Slack.ConversationsRepliesError = null;
        EnqueueReplies(new SlackConversationsRepliesPage(
            true,
            null,
            new[] { Message("1710000000.000720", "U_X", "context") },
            null));

        var second = await PostChannelMentionAsync(
            connection,
            conversationId,
            secondMentionTs,
            threadTs: rootTs,
            text: "<@U123> second attempt");
        Assert.Equal("accepted", second.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task EmptyMentionInExistingThread_RejectsBeforeInboxAcceptance()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-thread-empty";
        const string rootTs = "1710000000.000800";
        const string mentionTs = "1710000000.000850";

        var result = await PostChannelMentionAsync(
            connection,
            conversationId,
            mentionTs,
            threadTs: rootTs,
            text: "<@U123>");

        Assert.Equal("rejected", result.GetProperty("kind").GetString());
        Assert.Equal("Please send a task for the Agent to perform.", result.GetProperty("reason").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == conversationId
                && row.SlackMessageIdentity.EndsWith(mentionTs))
            .ToListAsync());
        Assert.Empty(await db.SlackThreadLaunchReservations
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == conversationId
                && row.ThreadTs == rootTs)
            .ToListAsync());
    }

    [Fact]
    public async Task BoundThreadMention_IsFollowup_AndReadsNoHistory()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-thread-bound";
        const string rootTs = "1710000000.000900";
        const string firstTs = "1710000000.000900";
        const string followupTs = "1710000000.000910";
        var first = await PostChannelMentionAsync(
            connection,
            conversationId,
            firstTs,
            threadTs: null,
            text: "<@U123> first task");
        var firstSessionId = first.GetProperty("sessionId").GetString();
        Assert.NotNull(firstSessionId);

        var followup = await PostChannelMentionAsync(
            connection,
            conversationId,
            followupTs,
            threadTs: rootTs,
            mentions: Array.Empty<string>(),
            text: "follow-up question");

        Assert.Equal(firstSessionId, followup.GetProperty("sessionId").GetString());
        Assert.True(followup.GetProperty("followup").GetBoolean());
    }

    [Fact]
    public async Task FirstMentionAcceptanceReply_NotesImportedBackground()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-thread-reply";
        const string rootTs = "1710000000.001000";
        const string mentionTs = "1710000000.001050";
        EnqueueReplies(new SlackConversationsRepliesPage(
            true,
            null,
            new[] { Message("1710000000.001020", "U_X", "original") },
            null));

        var result = await PostChannelMentionAsync(
            connection,
            conversationId,
            mentionTs,
            threadTs: rootTs,
            text: "<@U123> summarize");
        Assert.Equal("accepted", result.GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var replies = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == conversationId
                && row.ThreadTs == rootTs
                && row.Kind == SlackOutboxKinds.UserAction)
            .Select(row => row.PayloadJson)
            .ToListAsync();
        var latest = replies.Last();
        Assert.Contains("background", latest, StringComparison.OrdinalIgnoreCase);
    }

    private static SlackConversationMessage Message(string ts, string user, string text) =>
        new(
            Type: "message",
            Subtype: null,
            Ts: ts,
            User: user,
            Text: text,
            BotId: null,
            ThreadTs: null,
            ParentUserId: null);

    private async Task<JsonElement> PostChannelMentionAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string? threadTs,
        string text,
        string[]? mentions = null)
    {
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            threadTs,
            mentionedUserIds = mentions ?? new[] { connection.BotUserId },
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text,
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        response.EnsureSuccessStatusCode();
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static string IngressPath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress";

    private void EnqueueReplies(SlackConversationsRepliesPage page)
    {
        _fixture.Slack.ConversationsRepliesError = null;
        _fixture.Slack.ConversationsRepliesPages.Clear();
        _fixture.Slack.ConversationsRepliesPages.Enqueue(page);
    }

    private async Task<AgentConnection> CreateConnectionAsync(string agentNameSuffix = "")
    {
        var id = $"connection_{Guid.NewGuid():N}";
        var projectId = $"project_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();
        _fixture.Slack.UsersInfo = new(true, null, new("U_OWNER", "T123", false, false, false, false, false));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        var botUserId = string.IsNullOrEmpty(agentNameSuffix) ? "U123" : $"U{agentNameSuffix.GetHashCode():X}".PadRight(8, '0').Substring(0, 8);
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = projectId,
            Name = $"Mohist Agent {agentNameSuffix}",
            Status = AgentStatus.Active,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = projectId,
                Name = $"Mohist Agent {agentNameSuffix}",
                Status = AgentStatus.Active,
                AgentConfig = JsonSerializer.SerializeToElement(new { model = "openai/gpt-4o", runtime = "opencode" }),
            }),
        });
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = id,
            ProjectId = projectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = "T123",
            AppId = "A123",
            BotUserId = botUserId,
            BotName = $"Mohist {agentNameSuffix}".Trim(),
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            OwnerSlackUserId = "U_OWNER",
            LastHeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp"));
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb"));
        return new AgentConnection
        {
            Id = id,
            ProjectId = projectId,
            WorkspaceTeamId = "T123",
            BotUserId = botUserId,
        };
    }
}
