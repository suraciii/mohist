using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// Covers the issue-526 access-policy substrate. The Owner-only
/// access decision is the substrate that the wider policy work in
/// T-002/T-003 layers on top of; this file pins the behavior under
/// the default <c>owner_only</c> policy so a future widening
/// (allowlist, anyone) cannot silently regress the Owner path.
/// </summary>
[Collection("MohistIntegration")]
public sealed class SlackAccessPolicySpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackAccessPolicySpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task New_connection_defaults_to_owner_only_policy()
    {
        var connection = await CreateConnectionAsync();

        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<AgentConnectionStore>();
        var fetched = await store.GetAsync(connection.ProjectId, connection.Id);
        Assert.NotNull(fetched);
        Assert.Equal(AccessPolicyKind.OwnerOnly, fetched!.AccessPolicy);
    }

    [Fact]
    public async Task Owner_root_mention_under_default_owner_only_is_accepted()
    {
        var connection = await CreateConnectionAsync();
        var data = await PostChannelAsync(
            connection,
            conversationId: "C-access-owner-accept",
            messageTs: "1710000000.100100",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_OWNER",
            text: "<@U123> please summarise this");

        Assert.Equal("accepted", data.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("sessionId").GetString()));
    }

    [Fact]
    public async Task Non_owner_root_mention_under_default_owner_only_is_rejected_with_no_resources()
    {
        var connection = await CreateConnectionAsync();
        var data = await PostChannelAsync(
            connection,
            conversationId: "C-access-non-owner-reject",
            messageTs: "1710000000.100110",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_OTHER",
            text: "<@U123> I should not be able to invoke");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Contains("owner", data.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-access-non-owner-reject")
            .ToListAsync());
        Assert.Empty(await db.AgentSessions
            .Where(row => row.LabelConnectionId == connection.Id)
            .ToListAsync());
    }

    [Fact]
    public async Task Non_owner_bound_thread_reply_under_default_owner_only_is_rejected_with_no_resources()
    {
        var connection = await CreateConnectionAsync();
        var root = await PostChannelAsync(
            connection,
            conversationId: "C-access-thread-reject",
            messageTs: "1710000000.100200",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_OWNER",
            text: "<@U123> first task");
        var sessionId = root.GetProperty("sessionId").GetString()!;
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId)
            .MarkTurnTerminalAsync(
                root.GetProperty("turnId").GetString()!,
                AgentTurnStatus.Completed,
                null);

        var data = await PostChannelAsync(
            connection,
            conversationId: "C-access-thread-reject",
            messageTs: "1710000000.100210",
            threadTs: "1710000000.100200",
            mentions: Array.Empty<string>(),
            senderSlackUserId: "U_OTHER",
            text: "follow-up from non-owner");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-access-thread-reject"
                && row.SlackMessageIdentity.EndsWith("1710000000.100210"))
            .ToListAsync());
        var session = await db.AgentSessions.SingleAsync(row => row.Id == sessionId);
        Assert.Equal("1710000000.100200", session.LabelSlackThreadTs);
    }

    [Fact]
    public async Task Non_owner_dm_under_default_owner_only_is_rejected()
    {
        var connection = await CreateConnectionAsync();
        var data = await PostDmAsync(
            connection,
            conversationId: "D-access-non-owner",
            messageTs: "1710000000.100300",
            senderSlackUserId: "U_OTHER",
            text: "DM from non-owner");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Contains("owner", data.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Owner_dm_under_default_owner_only_is_accepted()
    {
        var connection = await CreateConnectionAsync();
        var data = await PostDmAsync(
            connection,
            conversationId: "D-access-owner-accept",
            messageTs: "1710000000.100310",
            senderSlackUserId: "U_OWNER",
            text: "DM from owner: a real task");

        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("kind").GetString()));
        Assert.NotEqual("rejected", data.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Allowed_members_table_cascade_deletes_with_the_connection()
    {
        var connection = await CreateConnectionAsync();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            db.SlackConnectionAllowedMembers.Add(new SlackConnectionAllowedMemberRow
            {
                Id = $"slkalm_{Guid.NewGuid():N}",
                ProjectId = connection.ProjectId,
                ConnectionId = connection.Id,
                SlackUserId = "U_LISTED",
                WorkspaceTeamId = connection.WorkspaceTeamId,
                CreatedAt = _fixture.TimeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<AgentConnectionStore>();
            await store.DeleteAsync(connection.ProjectId, connection.Id);
        }

        await using var verify = _fixture.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await verifyDb.SlackConnectionAllowedMembers
            .Where(row => row.ConnectionId == connection.Id)
            .ToListAsync());
    }

    [Fact]
    public async Task Allowed_members_store_queryable_under_owner_only_path()
    {
        var connection = await CreateConnectionAsync();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            db.SlackConnectionAllowedMembers.Add(new SlackConnectionAllowedMemberRow
            {
                Id = $"slkalm_{Guid.NewGuid():N}",
                ProjectId = connection.ProjectId,
                ConnectionId = connection.Id,
                SlackUserId = "U_LISTED",
                WorkspaceTeamId = connection.WorkspaceTeamId,
                CreatedAt = _fixture.TimeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        await using var read = _fixture.Services.CreateAsyncScope();
        var store = read.ServiceProvider.GetRequiredService<SlackConnectionAllowedMemberStore>();
        Assert.True(await store.IsAllowedAsync(connection.ProjectId, connection.Id, "U_LISTED"));
        Assert.False(await store.IsAllowedAsync(connection.ProjectId, connection.Id, "U_OTHER"));
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

    private async Task<AgentConnection> CreateConnectionAsync()
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
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp"));
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb"));
        return new AgentConnection
        {
            Id = id,
            ProjectId = projectId,
            WorkspaceTeamId = "T123",
            BotUserId = "U123",
        };
    }
}
