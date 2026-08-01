using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[CollectionDefinition("SlackAccessPolicyT003", DisableParallelization = true)]
public class SlackAccessPolicyT003Collection : ICollectionFixture<MohistIntegrationFixture>;

[Collection("SlackAccessPolicyT003")]
public sealed class SlackAccessPolicyT003Specs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackAccessPolicyT003Specs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _fixture.Slack.UsersInfoByUser.Clear();
        _fixture.Slack.DefaultUsersInfo = new(true, null, new("U_OWNER", "T123", false, false, false, false, false));
        _fixture.Slack.ConversationsInfoResponses.Clear();
        _fixture.Slack.ConversationsInfoCalls.Clear();
        _fixture.Slack.UsersInfoCalls.Clear();
        _fixture.Slack.UsersInfoResolver = null;
        _fixture.Slack.ConversationsInfoResolver = null;
        _fixture.Slack.UsersListPages.Clear();
    }

    [Fact]
    public async Task ManageAccess_Allowlist_ReplacesAllowlist_AndReAddsOwner()
    {
        var connection = await CreateConnectionAsync();
        await SeedMemberAsync(connection, "U_LISTED_A");
        await SeedMemberAsync(connection, "U_LISTED_B");
        _fixture.Slack.UsersInfoByUser["U_LISTED_A"] = Eligible("U_LISTED_A", "Ada");
        _fixture.Slack.UsersInfoByUser["U_LISTED_B"] = Eligible("U_LISTED_B", "Bore");
        _fixture.Slack.UsersInfoByUser["U_KEPT"] = Eligible("U_KEPT", "Kept");

        var data = await ManageAsync(connection, AccessPolicyKind.Allowlist, new[] { "U_KEPT" });
        Assert.Equal(AccessPolicyKind.Allowlist, data.GetProperty("accessPolicy").GetString());
        var members = data.GetProperty("allowMembers").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "U_KEPT" }, members);
        Assert.Equal(SlackConnectionAccessContract.AnyoneDisclosure, data.GetProperty("anyoneDisclosure").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SlackConnectionAllowedMemberStore>();
        var stored = await store.ListAsync(connection.ProjectId, connection.Id);
        Assert.Equal(new[] { "U_KEPT" }, stored);
    }

    [Fact]
    public async Task ManageAccess_OwnerOnly_DiscardsExistingAllowlist()
    {
        var connection = await CreateConnectionAsync();
        await SeedMemberAsync(connection, "U_LISTED");
        _fixture.Slack.UsersInfoByUser["U_LISTED"] = Eligible("U_LISTED");

        var data = await ManageAsync(connection, AccessPolicyKind.OwnerOnly, Array.Empty<string>());
        Assert.Equal(AccessPolicyKind.OwnerOnly, data.GetProperty("accessPolicy").GetString());
        Assert.Empty(data.GetProperty("allowMembers").EnumerateArray());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SlackConnectionAllowedMemberStore>();
        Assert.Empty(await store.ListAsync(connection.ProjectId, connection.Id));
    }

    [Fact]
    public async Task ManageAccess_Anyone_RejectsAllowMembersBeforeMutation()
    {
        var connection = await CreateConnectionAsync();
        var response = await RawManageAsync(connection, AccessPolicyKind.Anyone, new[] { "U_OTHER" }, expectSuccess: false);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await ReadErrorAsync(response);
        Assert.Equal("allow_members_not_allowed", body.GetProperty("code").GetString());
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SlackConnectionAllowedMemberStore>();
        Assert.Empty(await store.ListAsync(connection.ProjectId, connection.Id));
    }

    [Fact]
    public async Task ManageAccess_OwnerOnly_WithAllowMembers_RejectsBeforeMutation()
    {
        var connection = await CreateConnectionAsync();
        var response = await RawManageAsync(connection, AccessPolicyKind.OwnerOnly, new[] { "U_OTHER" }, expectSuccess: false);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadErrorAsync(response);
        Assert.Equal("allow_members_not_allowed", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ManageAccess_BotMember_IsRejected()
    {
        var connection = await CreateConnectionAsync();
        _fixture.Slack.UsersInfoByUser["U_BOT"] = new(true, null, new("U_BOT", "T123", true, false, false, false, false));
        var response = await RawManageAsync(connection, AccessPolicyKind.Allowlist, new[] { "U_BOT" }, expectSuccess: false);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadErrorAsync(response);
        Assert.Equal("invalid_allow_member", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ManageAccess_OwnerId_AsAllowMember_IsAcceptedAndNotStored()
    {
        var connection = await CreateConnectionAsync();
        _fixture.Slack.UsersInfoByUser["U_OWNER"] = Eligible("U_OWNER");
        var data = await ManageAsync(connection, AccessPolicyKind.Allowlist, new[] { "U_OWNER" });
        Assert.Equal(AccessPolicyKind.Allowlist, data.GetProperty("accessPolicy").GetString());
        var stored = data.GetProperty("allowMembers").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.DoesNotContain("U_OWNER", stored);
        Assert.Empty(stored);
    }

    [Fact]
    public async Task ManageAccess_Allowlist_AcceptsListedMember_SubsequentInput_Accepted()
    {
        var connection = await CreateConnectionAsync();
        _fixture.Slack.UsersInfoByUser["U_LISTED"] = Eligible("U_LISTED");
        await ManageAsync(connection, AccessPolicyKind.Allowlist, new[] { "U_LISTED" });
        _fixture.Slack.UsersInfoByUser["U_LISTED"] = Eligible("U_LISTED");
        var status = await PostChannelAsync(connection, "U_LISTED", "C-managed-allowlist-accept", "1710000600.100100");
        Assert.Equal("accepted", status);
    }

    [Fact]
    public async Task ManageAccess_Allowlist_AfterRemoval_NextInputRejected()
    {
        var connection = await CreateConnectionAsync();
        _fixture.Slack.UsersInfoByUser["U_LISTED"] = Eligible("U_LISTED");
        await ManageAsync(connection, AccessPolicyKind.Allowlist, new[] { "U_LISTED" });
        _fixture.Slack.UsersInfoByUser["U_LISTED"] = Eligible("U_LISTED");
        var accepted = await PostChannelAsync(connection, "U_LISTED", "C-managed-allowlist-remove", "1710000601.100100");
        Assert.Equal("accepted", accepted);

        await ManageAsync(connection, AccessPolicyKind.Allowlist, Array.Empty<string>());
        _fixture.Slack.UsersInfoByUser["U_LISTED"] = Eligible("U_LISTED");
        var rejected = await PostChannelAsync(connection, "U_LISTED", "C-managed-allowlist-remove", "1710000601.100200");
        Assert.Equal("rejected", rejected);
    }

    [Fact]
    public async Task ManageAccess_MembersSearch_ResolvesToEligibleMemberIds()
    {
        var connection = await CreateConnectionAsync();
        _fixture.Slack.UsersListPages.Clear();
        _fixture.Slack.UsersListPages.Enqueue(new SlackUsersListResponse(
            true, null, new[]
            {
                new SlackUserInfo("U_OWNER", "T123", false, false, false, false, false, DisplayName: "Owner Display", RealName: "Owner Real"),
                new SlackUserInfo("U_MATCH", "T123", false, false, false, false, false, DisplayName: "Match Display"),
                new SlackUserInfo("U_GUEST", "T123", false, false, true, false, true, DisplayName: "Guest User"),
                new SlackUserInfo("U_OTHER_TEAM", "T_OTHER", false, false, false, false, false, DisplayName: "Other Team"),
            }, null));

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/members?q=Match");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var members = doc.RootElement.GetProperty("data").GetProperty("members").EnumerateArray()
            .Select(e => e.GetProperty("slackUserId").GetString()).ToArray();
        Assert.Equal(new[] { "U_MATCH" }, members);
    }

    [Fact]
    public async Task ManageAccess_UnknownPolicy_400()
    {
        var connection = await CreateConnectionAsync();
        var response = await RawManageAsync(connection, "bogus", Array.Empty<string>(), expectSuccess: false);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadErrorAsync(response);
        Assert.Equal("invalid_access_policy", body.GetProperty("code").GetString());
    }

    private async Task<JsonElement> ManageAsync(AgentConnection connection, string policy, IReadOnlyList<string> members)
    {
        var response = await RawManageAsync(connection, policy, members, expectSuccess: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private async Task<HttpResponseMessage> RawManageAsync(
        AgentConnection connection,
        string policy,
        IReadOnlyList<string> members,
        bool expectSuccess)
    {
        var path = $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/manage-access";
        var body = new
        {
            accessPolicy = policy,
            allowMembers = members,
        };
        return await _fixture.Client.PostAsJsonAsync(path, body);
    }

    private async Task<JsonElement> ReadErrorAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private async Task SeedMemberAsync(AgentConnection connection, string slackUserId)
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

    private string _currentProjectId = string.Empty;
    private string _currentConnectionId = string.Empty;
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
            }, Mohist.Server.Infrastructure.JSON.Options),
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

        _currentProjectId = projectId;
        _currentConnectionId = id;
        return new AgentConnection
        {
            Id = id,
            ProjectId = projectId,
            WorkspaceTeamId = "T123",
            BotUserId = "U123",
        };
    }

    private async Task<string> PostChannelAsync(AgentConnection connection, string senderSlackUserId, string conversationId, string messageTs)
    {
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            threadTs = (string?)null,
            mentionedUserIds = new[] { connection.BotUserId },
            senderSlackUserId,
            senderKind = "human",
            text = $"<@{connection.BotUserId}> please summarise",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress",
            body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("kind").GetString()!;
    }

    private static SlackUserInfoResponse Eligible(string userId, string? displayName = null) =>
        new(true, null, new(userId, "T123", false, false, false, false, false, DisplayName: displayName));
}
