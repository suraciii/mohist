using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("MohistIntegration")]
public sealed class SlackChannelThreadIngressSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackChannelThreadIngressSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Owner_root_mention_launches_work_and_binds_thread_to_session()
    {
        var connection = await CreateConnectionAsync();
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-channel-A",
            messageTs = "1710000000.000100",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connection.BotUserId },
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text = "<@U123> summarize the bug",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        var sessionId = data.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrEmpty(sessionId));
        Assert.Equal("1710000000.000100", data.GetProperty("threadRoot").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        var stored = await threadMapping.GetSessionIdAsync(
            connection.ProjectId, connection.WorkspaceTeamId, connection.Id,
            "C-channel-A", "1710000000.000100");
        Assert.Equal(sessionId, stored);
        Assert.NotNull(data.GetProperty("jobKey").GetString());
    }

    [Fact]
    public async Task Bare_root_mention_with_no_task_creates_no_resources_and_asks_for_task()
    {
        var connection = await CreateConnectionAsync();
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-channel-A",
            messageTs = "1710000000.000110",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connection.BotUserId },
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text = "<@U123>",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Equal("Please send a task for the Agent to perform.",
            data.GetProperty("reason").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        Assert.Null(await threadMapping.GetSessionIdAsync(
            connection.ProjectId, connection.WorkspaceTeamId, connection.Id,
            "C-channel-A", "1710000000.000110"));
        var inbox = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await inbox.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-A"
                && row.SlackMessageIdentity.EndsWith("1710000000.000110"))
            .ToListAsync());
    }

    [Fact]
    public async Task Followup_after_terminal_continues_bound_session()
    {
        var connection = await CreateConnectionAsync();
        var first = await PostChannelAsync(connection, "C-channel-B",
            messageTs: "1710000000.000200",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> first task");
        var firstSessionId = first.GetProperty("sessionId").GetString();

        await _fixture.Grains.GetGrain<IAgentSessionGrain>(firstSessionId!)
            .MarkTurnTerminalAsync(
                first.GetProperty("turnId").GetString()!,
                AgentTurnStatus.Completed,
                null);

        var followup = await PostChannelAsync(connection, "C-channel-B",
            messageTs: "1710000000.000210",
            threadTs: "1710000000.000200",
            mentions: Array.Empty<string>(),
            text: "follow-up question");
        Assert.Equal(firstSessionId, followup.GetProperty("sessionId").GetString());
        Assert.True(followup.GetProperty("followup").GetBoolean());
    }

    [Fact]
    public async Task Followup_during_execution_acknowledges_queued()
    {
        var connection = await CreateConnectionAsync();
        var first = await PostChannelAsync(connection, "C-channel-C",
            messageTs: "1710000000.000300",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> long task");
        var firstSessionId = first.GetProperty("sessionId").GetString();

        var followup = await PostChannelAsync(connection, "C-channel-C",
            messageTs: "1710000000.000310",
            threadTs: "1710000000.000300",
            mentions: Array.Empty<string>(),
            text: "more details");

        Assert.Equal(firstSessionId, followup.GetProperty("sessionId").GetString());
        Assert.True(followup.GetProperty("followup").GetBoolean());

        var reply = await ReadOutboxReplyAsync(connection, "C-channel-C", "1710000000.000310");
        Assert.Contains("Continuing", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_owner_mention_is_rejected_with_no_agent_resources()
    {
        var connection = await CreateConnectionAsync();
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-channel-D",
            messageTs = "1710000000.000400",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connection.BotUserId },
            senderSlackUserId = "U_OTHER",
            senderKind = "human",
            text = "<@U123> do something",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Contains("owner", data.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await inbox.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-D")
            .ToListAsync());
        Assert.Empty(await inbox.AgentSessions.Where(row => row.LabelConnectionId == connection.Id).ToListAsync());
    }

    [Fact]
    public async Task Bot_sender_is_acknowledged_and_ignored()
    {
        var connection = await CreateConnectionAsync();
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-channel-E",
            messageTs = "1710000000.000500",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connection.BotUserId },
            senderSlackUserId = "U_BOT",
            senderKind = "bot",
            text = "<@U123> ignorable",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ignored", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await inbox.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-E")
            .ToListAsync());
    }

    [Fact]
    public async Task Unknown_sender_is_acknowledged_and_ignored()
    {
        var connection = await CreateConnectionAsync();
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-channel-E2",
            messageTs = "1710000000.000510",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connection.BotUserId },
            senderSlackUserId = (string?)null,
            senderKind = "unknown",
            text = "<@U123> ignorable",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ignored", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Plain_channel_message_with_no_mention_is_ignored_without_persisting_text()
    {
        var connection = await CreateConnectionAsync();
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-channel-F",
            messageTs = "1710000000.000600",
            threadTs = (string?)null,
            mentionedUserIds = Array.Empty<string>(),
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text = "no mention here",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ignored", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await inbox.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-F")
            .ToListAsync());
    }

    [Fact]
    public async Task Unbound_thread_reply_without_mention_is_ignored()
    {
        var connection = await CreateConnectionAsync();
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-channel-G",
            messageTs = "1710000000.000700",
            threadTs = "1710000000.000690",
            mentionedUserIds = Array.Empty<string>(),
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text = "just chatting",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ignored", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await inbox.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-G")
            .ToListAsync());
    }

    [Fact]
    public async Task Another_connection_ignores_unmentioned_reply_to_bound_thread()
    {
        var connection = await CreateConnectionAsync();
        var first = await PostChannelAsync(connection, "C-channel-H",
            messageTs: "1710000000.000800",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> do it");
        var sessionId = first.GetProperty("sessionId").GetString();

        var otherConnection = await CreateConnectionAsync("agent-other");
        var body = new
        {
            isDirectMessage = false,
            teamId = otherConnection.WorkspaceTeamId,
            conversationId = "C-channel-H",
            messageTs = "1710000000.000810",
            threadTs = "1710000000.000800",
            mentionedUserIds = Array.Empty<string>(),
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text = "follow-up not for you",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(otherConnection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ignored", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await inbox.SlackProviderInboxRows
            .Where(row => row.ConnectionId == otherConnection.Id
                && row.ConversationId == "C-channel-H")
            .ToListAsync());
        Assert.NotNull(sessionId);
    }

    [Fact]
    public async Task Provenance_labels_distinct_workspaces_with_equal_thread_ts_do_not_share_bindings()
    {
        var connectionA = await CreateConnectionAsync("agent-A");
        var connectionB = await CreateConnectionAsync("agent-B");

        await PostChannelAsync(connectionA, "C-channel-I",
            messageTs: "1710000000.000900",
            threadTs: null,
            mentions: new[] { connectionA.BotUserId },
            text: "<@U123> from workspace A");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        var fromA = await threadMapping.ListBindingsAsync(
            connectionA.ProjectId, connectionA.WorkspaceTeamId, "C-channel-I", "1710000000.000900");
        var fromB = await threadMapping.ListBindingsAsync(
            connectionB.ProjectId, connectionB.WorkspaceTeamId, "C-channel-I", "1710000000.000900");

        Assert.Single(fromA);
        Assert.Empty(fromB);
    }

    [Fact]
    public async Task Provenance_equal_root_ts_in_two_channels_stay_isolated()
    {
        var connection = await CreateConnectionAsync();
        await PostChannelAsync(connection, "C-channel-J1",
            messageTs: "1710000000.001000",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> first channel");
        await PostChannelAsync(connection, "C-channel-J2",
            messageTs: "1710000000.001000",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> second channel");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        var fromOne = await threadMapping.ListBindingsAsync(
            connection.ProjectId, connection.WorkspaceTeamId, "C-channel-J1", "1710000000.001000");
        var fromTwo = await threadMapping.ListBindingsAsync(
            connection.ProjectId, connection.WorkspaceTeamId, "C-channel-J2", "1710000000.001000");

        Assert.Single(fromOne);
        Assert.Single(fromTwo);
        Assert.NotEqual(fromOne[0].SessionId, fromTwo[0].SessionId);
    }

    [Fact]
    public async Task Crash_window_repair_rebinds_thread_from_persisted_session()
    {
        var connection = await CreateConnectionAsync();
        var first = await PostChannelAsync(connection, "C-channel-K",
            messageTs: "1710000000.001100",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> crash window");
        var originalSessionId = first.GetProperty("sessionId").GetString()!;

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
            await threadMapping.DeleteForConnectionAsync(connection.ProjectId, connection.Id);
        }

        var followup = await PostChannelAsync(connection, "C-channel-K",
            messageTs: "1710000000.001110",
            threadTs: "1710000000.001100",
            mentions: Array.Empty<string>(),
            text: "after restart");

        Assert.Equal(originalSessionId, followup.GetProperty("sessionId").GetString());
        Assert.True(followup.GetProperty("followup").GetBoolean());

        await using var verify = _fixture.Services.CreateAsyncScope();
        var reloaded = await verify.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>()
            .GetSessionIdAsync(connection.ProjectId, connection.WorkspaceTeamId, connection.Id,
                "C-channel-K", "1710000000.001100");
        Assert.Equal(originalSessionId, reloaded);
    }

    [Fact]
    public async Task Redelivered_root_mention_creates_no_duplicate_resources()
    {
        var connection = await CreateConnectionAsync();
        var first = await PostChannelAsync(connection, "C-channel-L",
            messageTs: "1710000000.001200",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> replay me");
        var firstSessionId = first.GetProperty("sessionId").GetString();

        var replay = await PostChannelAsync(connection, "C-channel-L",
            messageTs: "1710000000.001200",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> replay me");

        Assert.Equal(firstSessionId, replay.GetProperty("sessionId").GetString());
        Assert.Equal("queued", replay.GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var sessions = await db.AgentSessions
            .Where(row => row.LabelConnectionId == connection.Id
                && row.LabelSlackConversationId == "C-channel-L")
            .ToListAsync();
        Assert.Single(sessions);

        var inboxRows = await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-L"
                && row.SlackMessageIdentity.EndsWith("1710000000.001200"))
            .ToListAsync();
        Assert.Single(inboxRows);

        var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        var bindings = await threadMapping.ListBindingsAsync(
            connection.ProjectId, connection.WorkspaceTeamId,
            "C-channel-L", "1710000000.001200");
        Assert.Single(bindings);
    }

    [Fact]
    public async Task Threaded_launch_post_replies_are_addressed_into_thread()
    {
        var connection = await CreateConnectionAsync();
        var first = await PostChannelAsync(connection, "C-channel-M",
            messageTs: "1710000000.001300",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> post into thread");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var ack = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-M"
                && row.ThreadTs == "1710000000.001300")
            .Select(row => row.PayloadJson)
            .FirstOrDefaultAsync();
        Assert.NotNull(ack);
        Assert.Contains("Task accepted", ack!, StringComparison.Ordinal);
    }

    private async Task<JsonElement> PostChannelAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string? threadTs,
        string[] mentions,
        string text)
    {
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            threadTs,
            mentionedUserIds = mentions,
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text,
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        response.EnsureSuccessStatusCode();
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private async Task<string> ReadOutboxReplyAsync(AgentConnection connection, string conversationId, string messageTs)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var candidates = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == conversationId
                && row.ThreadTs == "1710000000.000300"
                && row.Kind == SlackOutboxKinds.UserAction)
            .Select(row => new { row.PayloadJson, row.CreatedAt })
            .ToListAsync();
        Assert.NotEmpty(candidates);
        var latest = candidates
            .OrderByDescending(row => row.CreatedAt)
            .First();
        return JsonDocument.Parse(latest.PayloadJson).RootElement.GetProperty("text").GetString()!;
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
            }, JSON.Options),
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

    private static string IngressPath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress";
}