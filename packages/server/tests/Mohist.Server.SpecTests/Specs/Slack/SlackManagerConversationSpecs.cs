using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("MohistIntegration")]
public sealed class SlackManagerConversationSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackManagerConversationSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Claimed_manager_conversation_uses_the_shared_status_and_cannot_delete_a_binding()
    {
        const string team = "T_MANAGER_CONVERSATION";
        const string appId = "A_MANAGER_CONVERSATION";
        const string owner = "U_MANAGER_CONVERSATION";
        var projectId = $"project_manager_conversation_{Guid.NewGuid():N}";
        await SeedProjectAsync(projectId);

        using var setupResponse = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/setup", new
        {
            workspaceTeamId = team,
            managerAppId = appId,
            managerBotUserId = "U_MANAGER_BOT_CONVERSATION",
            managerCredentialRef = "manager-credential-conversation",
        });
        setupResponse.EnsureSuccessStatusCode();
        var claimCode = (await ReadDataAsync(setupResponse)).GetProperty("claimCode").GetString()!;

        var unclaimed = await SendManagerMessageAsync(
            appId, team, owner, "1710000000.000001", "list");
        Assert.Equal("rejected", unclaimed.GetProperty("decision").GetString());
        Assert.False(unclaimed.GetProperty("deliveryIntentCreated").GetBoolean());

        var claimed = await SendManagerMessageAsync(
            appId, team, owner, "1710000000.000002", $"claim {claimCode}");
        Assert.Equal("accepted", claimed.GetProperty("decision").GetString());

        var created = await SendManagerMessageAsync(
            appId,
            team,
            owner,
            "1710000000.000003",
            $"create {projectId} release-helper review release changes");
        Assert.Equal("accepted", created.GetProperty("decision").GetString());

        var duplicate = await SendManagerMessageAsync(
            appId,
            team,
            owner,
            "1710000000.000003",
            $"create {projectId} release-helper review release changes");
        Assert.Equal("duplicate", duplicate.GetProperty("decision").GetString());

        using var statusResponse = await _fixture.Client.GetAsync($"/api/slack-manager/status?workspaceTeamId={team}");
        statusResponse.EnsureSuccessStatusCode();
        var status = await ReadDataAsync(statusResponse);
        var nextAction = status.GetProperty("nextAction").GetString()!;

        var listed = await SendManagerMessageAsync(appId, team, owner, "1710000000.000004", "list");
        Assert.Equal("accepted", listed.GetProperty("decision").GetString());

        var deletion = await SendManagerMessageAsync(appId, team, owner, "1710000000.000005", "permanent-delete");
        Assert.Equal("accepted", deletion.GetProperty("decision").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var agent = AgentStore.Deserialize(await database.Agents
            .Where(row => row.ProjectId == projectId && row.Name == "release-helper")
            .Select(row => row.State)
            .SingleAsync());
        Assert.NotNull(agent);
        Assert.Equal("opencode", agent!.AgentConfig!.Value.GetProperty("runtime").GetString());
        var connection = Assert.Single(await database.AgentConnections
            .Where(row => row.ProjectId == projectId && row.AgentId == agent.Id && row.WorkspaceTeamId == team)
            .ToListAsync());
        Assert.Null(connection.DeletedAt);
        var messages = await database.SlackOutboxRows
            .Where(row => row.OwnerKind == SlackDeliveryOwnerKinds.Manager)
            .Select(row => row.PayloadJson)
            .ToListAsync();
        Assert.Single(messages, payload => payload.Contains("Agent created and mounted.", StringComparison.Ordinal));
        Assert.Contains(messages, payload => payload.Contains($"Manager status: {nextAction}.", StringComparison.Ordinal));
        Assert.Contains(messages, payload => payload.Contains("manager_tool_not_available", StringComparison.Ordinal));
    }

    private async Task SeedProjectAsync(string projectId)
    {
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        database.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await database.SaveChangesAsync();
    }

    private async Task<JsonElement> SendManagerMessageAsync(
        string appId,
        string team,
        string sender,
        string messageTs,
        string text)
    {
        using var response = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/ingress", new
        {
            appId,
            workspaceTeamId = team,
            conversationId = "D_MANAGER_CONVERSATION",
            messageTs,
            senderSlackUserId = sender,
            text,
            isDirectMessage = true,
        });
        response.EnsureSuccessStatusCode();
        return await ReadDataAsync(response);
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }
}
