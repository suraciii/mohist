using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// Serializes the T-002 allowlist / anyone coverage. Each spec mutates
/// shared <see cref="RecordingSlackApiClient"/> state (per-user
/// <c>UsersInfo</c>, default <c>UsersInfo</c>, <c>ConversationsInfo</c>)
/// that races with the parallel <c>MohistIntegration</c> collection.
/// Running this collection non-parallel keeps the channel-membership and
/// workspace-member mocks stable while the new spec scenarios execute.
/// </summary>
[CollectionDefinition("SlackAccessPolicyT002", DisableParallelization = true)]
public class SlackAccessPolicyT002Collection : ICollectionFixture<MohistIntegrationFixture>;

/// <summary>
/// Full HTTP-path coverage for the issue-526 T-002 allowlist and anyone
/// branches. The unit-style branches live in
/// <see cref="SlackConnectionAccessDeciderSpecs"/>; this file pins the
/// wire-level ingress path so the five channel owner-check sites in
/// <c>SlackConnectionRoutes</c> are exercised under the wider policies.
/// </summary>
[Collection("SlackAccessPolicyT002")]
public sealed class SlackAccessPolicyT002Specs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackAccessPolicyT002Specs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _fixture.Slack.UsersInfoByUser.Clear();
        _fixture.Slack.DefaultUsersInfo = new(true, null, new("U_OWNER", "T123", false, false, false, false, false));
        _fixture.Slack.DefaultConversationsInfo = new(true, null, new("C-default", null, null, false, true));
        _fixture.Slack.ConversationsInfoResponses.Clear();
        _fixture.Slack.ConversationsInfoCalls.Clear();
        _fixture.Slack.UsersInfoCalls.Clear();
        _fixture.Slack.UsersInfoResolver = null;
        _fixture.Slack.ConversationsInfoResolver = null;
    }

    [Fact]
    public async Task Allowlist_listed_current_member_root_mention_is_accepted()
    {
        var connection = await CreateConnectionAsync();
        await SetAccessPolicyAsync(connection, AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        _fixture.Slack.UsersInfoByUser["U_LISTED"] = new(
            true, null, new("U_LISTED", "T123", false, false, false, false, false));

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-allowlist-listed-accept",
            messageTs: "1710000100.100100",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_LISTED",
            text: "<@U123> please summarise this");

        Assert.Equal("accepted", data.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("sessionId").GetString()));
    }

    [Fact]
    public async Task Allowlist_unlisted_member_root_mention_is_rejected_without_resources()
    {
        var connection = await CreateConnectionAsync();
        await SetAccessPolicyAsync(connection, AccessPolicyKind.Allowlist);

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-allowlist-unlisted-reject",
            messageTs: "1710000100.100110",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_UNLISTED",
            text: "<@U123> I am not on the list");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-allowlist-unlisted-reject")
            .ToListAsync());
        Assert.Empty(await db.AgentSessions
            .Where(row => row.LabelConnectionId == connection.Id
                && row.LabelSlackConversationId == "C-allowlist-unlisted-reject")
            .ToListAsync());
    }

    [Fact]
    public async Task Allowlist_listed_member_who_has_become_a_guest_is_rejected()
    {
        var connection = await CreateConnectionAsync();
        await SetAccessPolicyAsync(connection, AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        _fixture.Slack.UsersInfoByUser["U_LISTED"] = new(
            true, null, new("U_LISTED", "T123", false, false, false, false, true));

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-allowlist-downgraded-reject",
            messageTs: "1710000100.100120",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_LISTED",
            text: "<@U123> I am now a guest");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-allowlist-downgraded-reject")
            .ToListAsync());
    }

    [Fact]
    public async Task Anyone_workspace_member_in_channel_bot_can_see_is_accepted()
    {
        var connection = await CreateConnectionAsync();
        await SetAccessPolicyAsync(connection, AccessPolicyKind.Anyone);
        _fixture.Slack.UsersInfoByUser["U_OTHER"] = new(
            true, null, new("U_OTHER", "T123", false, false, false, false, false));
        _fixture.Slack.DefaultConversationsInfo = new(true, null, new("C-anyone-accept", null, null, false, true));

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-anyone-accept",
            messageTs: "1710000200.100100",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_OTHER",
            text: "<@U123> anyone should let me through");

        Assert.Equal("accepted", data.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("sessionId").GetString()));
    }

    [Fact]
    public async Task Anyone_workspace_member_in_channel_bot_cannot_see_is_rejected_without_resources()
    {
        var connection = await CreateConnectionAsync();
        await SetAccessPolicyAsync(connection, AccessPolicyKind.Anyone);
        _fixture.Slack.UsersInfoByUser["U_OTHER"] = new(
            true, null, new("U_OTHER", "T123", false, false, false, false, false));
        _fixture.Slack.DefaultConversationsInfo = new(true, null, new("C-anyone-reject", null, null, false, false));

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-anyone-reject",
            messageTs: "1710000200.100110",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_OTHER",
            text: "<@U123> bot is not in this channel");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-anyone-reject")
            .ToListAsync());
    }

    [Fact]
    public async Task Anyone_guest_root_mention_is_rejected_without_channel_check()
    {
        var connection = await CreateConnectionAsync();
        await SetAccessPolicyAsync(connection, AccessPolicyKind.Anyone);
        _fixture.Slack.UsersInfoByUser["U_GUEST"] = new(
            true, null, new("U_GUEST", "T123", false, false, false, false, true));

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-anyone-guest-reject",
            messageTs: "1710000200.100120",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_GUEST",
            text: "<@U123> I am just a guest");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Empty(_fixture.Slack.ConversationsInfoCalls);
    }

    [Fact]
    public async Task Anyone_external_participant_is_rejected()
    {
        var connection = await CreateConnectionAsync();
        await SetAccessPolicyAsync(connection, AccessPolicyKind.Anyone);
        _fixture.Slack.UsersInfoByUser["U_EXT"] = new(
            true, null, new("U_EXT", "T_OTHER", false, false, false, false, false));

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-anyone-external-reject",
            messageTs: "1710000200.100130",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_EXT",
            text: "<@U123> I am from another workspace");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Anyone_slack_users_info_failure_denies_unverifiable_sender()
    {
        var connection = await CreateConnectionAsync();
        await SetAccessPolicyAsync(connection, AccessPolicyKind.Anyone);
        _fixture.Slack.UsersInfoResolver = _ => throw new InvalidOperationException("simulated outage");

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-anyone-outage-reject",
            messageTs: "1710000200.100140",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_OTHER",
            text: "<@U123> slack is down");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Non_owner_dm_under_allowlist_is_rejected()
    {
        var connection = await CreateConnectionAsync();
        await SetAccessPolicyAsync(connection, AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        _fixture.Slack.UsersInfoByUser["U_LISTED"] = new(
            true, null, new("U_LISTED", "T123", false, false, false, false, false));

        var data = await PostDmAsync(
            connection,
            conversationId: "D-allowlist-non-owner-dm",
            messageTs: "1710000300.100100",
            senderSlackUserId: "U_LISTED",
            text: "DM from listed member");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Non_owner_dm_under_anyone_is_rejected()
    {
        var connection = await CreateConnectionAsync();
        await SetAccessPolicyAsync(connection, AccessPolicyKind.Anyone);
        _fixture.Slack.UsersInfoByUser["U_OTHER"] = new(
            true, null, new("U_OTHER", "T123", false, false, false, false, false));

        var data = await PostDmAsync(
            connection,
            conversationId: "D-anyone-non-owner-dm",
            messageTs: "1710000300.100110",
            senderSlackUserId: "U_OTHER",
            text: "DM from anyone");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Tightening_to_owner_only_rejects_subsequent_allowlisted_member_input()
    {
        var connection = await CreateConnectionAsync();
        await SetAccessPolicyAsync(connection, AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        _fixture.Slack.UsersInfoByUser["U_LISTED"] = new(
            true, null, new("U_LISTED", "T123", false, false, false, false, false));

        var accepted = await PostChannelAsync(
            connection,
            conversationId: "C-tighten-first",
            messageTs: "1710000400.100100",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_LISTED",
            text: "<@U123> tighten-before");
        Assert.Equal("accepted", accepted.GetProperty("kind").GetString());

        await SetAccessPolicyAsync(connection, AccessPolicyKind.OwnerOnly);

        var rejected = await PostChannelAsync(
            connection,
            conversationId: "C-tighten-first",
            messageTs: "1710000400.100110",
            threadTs: "1710000400.100100",
            mentions: Array.Empty<string>(),
            senderSlackUserId: "U_LISTED",
            text: "follow-up after tightening");

        Assert.Equal("rejected", rejected.GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var session = await db.AgentSessions.SingleAsync(row => row.LabelConnectionId == connection.Id);
        Assert.Equal("1710000400.100100", session.LabelSlackThreadTs);
    }

    [Fact]
    public async Task Loosening_to_allowlist_accepts_a_newly_listed_member_input()
    {
        var connection = await CreateConnectionAsync();

        var rejectedFirst = await PostChannelAsync(
            connection,
            conversationId: "C-loosen-first",
            messageTs: "1710000500.100100",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_NEW",
            text: "<@U123> owner_only should reject");
        Assert.Equal("rejected", rejectedFirst.GetProperty("kind").GetString());

        await SetAccessPolicyAsync(connection, AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_NEW");
        _fixture.Slack.UsersInfoByUser["U_NEW"] = new(
            true, null, new("U_NEW", "T123", false, false, false, false, false));

        var accepted = await PostChannelAsync(
            connection,
            conversationId: "C-loosen-second",
            messageTs: "1710000500.100110",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_NEW",
            text: "<@U123> allowlist accepts me now");

        Assert.Equal("accepted", accepted.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Removing_an_allowlist_member_rejects_their_next_input_only()
    {
        var connection = await CreateConnectionAsync();
        await SetAccessPolicyAsync(connection, AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        _fixture.Slack.UsersInfoByUser["U_LISTED"] = new(
            true, null, new("U_LISTED", "T123", false, false, false, false, false));

        var accepted = await PostChannelAsync(
            connection,
            conversationId: "C-remove-listed",
            messageTs: "1710000600.100100",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_LISTED",
            text: "<@U123> first message");
        Assert.Equal("accepted", accepted.GetProperty("kind").GetString());

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            await db.SlackConnectionAllowedMembers
                .Where(row => row.ConnectionId == connection.Id && row.SlackUserId == "U_LISTED")
                .ExecuteDeleteAsync();
        }

        var rejected = await PostChannelAsync(
            connection,
            conversationId: "C-remove-listed",
            messageTs: "1710000600.100110",
            threadTs: "1710000600.100100",
            mentions: Array.Empty<string>(),
            senderSlackUserId: "U_LISTED",
            text: "follow-up after removal");

        Assert.Equal("rejected", rejected.GetProperty("kind").GetString());

        await using var verify = _fixture.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        var session = await verifyDb.AgentSessions.SingleAsync(row => row.LabelConnectionId == connection.Id);
        Assert.Equal("1710000600.100100", session.LabelSlackThreadTs);
    }

    [Fact]
    public async Task Different_member_with_same_display_name_is_evaluated_on_own_identity()
    {
        var connection = await CreateConnectionAsync();
        await SetAccessPolicyAsync(connection, AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        _fixture.Slack.UsersInfoByUser["U_LISTED"] = new(
            true, null, new("U_LISTED", "T123", false, false, false, false, false));
        _fixture.Slack.UsersInfoByUser["U_NAMECLONE"] = new(
            true, null, new("U_NAMECLONE", "T123", false, false, false, false, false));

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-nameclone-reject",
            messageTs: "1710000700.100100",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_NAMECLONE",
            text: "<@U123> I share a display name but not identity");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
    }

    private async Task<JsonElement> PostChannelAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string? threadTs,
        string[] mentions,
        string senderSlackUserId,
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
            senderSlackUserId,
            senderKind = "human",
            text,
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private async Task<JsonElement> PostDmAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string senderSlackUserId,
        string text)
    {
        var body = new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            threadTs = (string?)null,
            mentionedUserIds = Array.Empty<string>(),
            senderSlackUserId,
            senderKind = "human",
            text,
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static string IngressPath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress";

    private async Task SetAccessPolicyAsync(AgentConnection connection, string policy)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<AgentConnectionStore>();
        await store.UpdateAsync(
            connection.ProjectId,
            connection.Id,
            new HashSet<string>(StringComparer.Ordinal) { "accessPolicy" },
            accessPolicy: policy);
    }

    private async Task SeedAllowedMemberAsync(AgentConnection connection, string slackUserId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.SlackConnectionAllowedMembers.Add(new SlackConnectionAllowedMemberRow
        {
            Id = $"slkalm_{Guid.NewGuid():N}",
            ProjectId = connection.ProjectId,
            ConnectionId = connection.Id,
            SlackUserId = slackUserId,
            WorkspaceTeamId = connection.WorkspaceTeamId,
            CreatedAt = _fixture.TimeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }

    private async Task<AgentConnection> CreateConnectionAsync()
    {
        var id = $"connection_{Guid.NewGuid():N}";
        var projectId = $"project_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = projectId,
            Name = "Mohist Agent",
            Status = AgentStatus.Active,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = projectId,
                Name = "Mohist Agent",
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
            BotUserId = "U123",
            BotName = "Mohist",
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
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.AppToken), System.Text.Encoding.UTF8.GetBytes("xapp"));
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.BotToken), System.Text.Encoding.UTF8.GetBytes("xoxb"));
        return new AgentConnection
        {
            Id = id,
            ProjectId = projectId,
            WorkspaceTeamId = "T123",
            BotUserId = "U123",
        };
    }
}