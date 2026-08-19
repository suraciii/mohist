using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using DomainAgent = Mohist.Server.Agent.Domain.Agent;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackConnectionIdentityPreviewSpecs
{
    private const string SlackAppCreationReference = "https://api.slack.com/apps?new_app=1";
    private readonly MohistIntegrationFixture _fixture;

    public SlackConnectionIdentityPreviewSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Create_without_BotName_persists_and_returns_the_Agent_identity_preview()
    {
        var seeded = await SeedAgentAsync("release_helper", "Reviews release changes.");

        using var response = await _fixture.Client.PostAsJsonAsync(Path(seeded.ProjectId), new
        {
            agentId = seeded.Agent.Id,
            workspaceTeamId = "T_FORGED",
            appId = "A_FORGED",
            botUserId = "U_FORGED",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = await ReadDataAsync(response);
        var connection = data.GetProperty("connection");
        var connectionId = connection.GetProperty("id").GetString()!;
        Assert.Equal("release_helper", data.GetProperty("botName").GetString());
        Assert.Equal("Reviews release changes.", data.GetProperty("appDescription").GetString());
        Assert.Equal(SlackAppCreationReference, data.GetProperty("slackAppCreationReference").GetString());
        Assert.Equal("release_helper", connection.GetProperty("botName").GetString());
        Assert.Equal(string.Empty, connection.GetProperty("workspaceTeamId").GetString());
        Assert.Equal(string.Empty, connection.GetProperty("appId").GetString());
        Assert.Equal(string.Empty, connection.GetProperty("botUserId").GetString());

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var persisted = await db.AgentConnections.SingleAsync(item => item.Id == connectionId);
            Assert.Equal("release_helper", persisted.BotName);
            Assert.Equal(string.Empty, persisted.WorkspaceTeamId);
            Assert.Equal(string.Empty, persisted.AppId);
            Assert.Equal(string.Empty, persisted.BotUserId);
        }

        using var detailResponse = await _fixture.Client.GetAsync(Path(seeded.ProjectId, connectionId));
        detailResponse.EnsureSuccessStatusCode();
        var detail = await ReadDataAsync(detailResponse);
        Assert.Equal(connectionId, detail.GetProperty("connection").GetProperty("id").GetString());
        Assert.Equal("release_helper", detail.GetProperty("botName").GetString());
        Assert.Equal("Reviews release changes.", detail.GetProperty("appDescription").GetString());
        Assert.Equal(SlackAppCreationReference, detail.GetProperty("slackAppCreationReference").GetString());
    }

    [Fact]
    public async Task Invalid_Agent_identity_gets_a_stable_name_suffix_and_description_fallback_without_mutation()
    {
        var seeded = await SeedAgentAsync("Release Helper!", " \t");
        var expected = SlackBotIdentityDeriver.Derive(seeded.Agent);

        using var response = await _fixture.Client.PostAsJsonAsync(
            Path(seeded.ProjectId),
            new { agentId = seeded.Agent.Id });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = await ReadDataAsync(response);
        var botName = data.GetProperty("botName").GetString()!;
        Assert.Equal(expected.BotName, botName);
        Assert.Matches("^release-helper-[0-9a-f]{8}$", botName);
        Assert.Matches("^[a-z0-9._-]{1,80}$", botName);
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("appDescription").GetString()));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.Agents.SingleAsync(item => item.Id == seeded.Agent.Id);
        var persistedAgent = AgentStore.Deserialize(row.State)!;
        Assert.Equal("Release Helper!", persistedAgent.Name);
        Assert.Equal(" \t", persistedAgent.Description);
    }

    [Fact]
    public async Task Create_with_explicit_BotName_preserves_the_caller_value()
    {
        var seeded = await SeedAgentAsync("agent_default", "Handles CLI requests.");

        using var response = await _fixture.Client.PostAsJsonAsync(
            Path(seeded.ProjectId),
            new { agentId = seeded.Agent.Id, botName = "CLI Helper" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = await ReadDataAsync(response);
        var connectionId = data.GetProperty("connection").GetProperty("id").GetString()!;
        Assert.Equal("CLI Helper", data.GetProperty("botName").GetString());
        Assert.Equal("CLI Helper", data.GetProperty("connection").GetProperty("botName").GetString());

        using var detailResponse = await _fixture.Client.GetAsync(Path(seeded.ProjectId, connectionId));
        detailResponse.EnsureSuccessStatusCode();
        var detail = await ReadDataAsync(detailResponse);
        Assert.Equal("CLI Helper", detail.GetProperty("botName").GetString());
        Assert.Equal("CLI Helper", detail.GetProperty("connection").GetProperty("botName").GetString());
    }

    private async Task<SeededAgent> SeedAgentAsync(string name, string description)
    {
        var projectId = $"project_{Guid.NewGuid():N}";
        var agent = new DomainAgent
        {
            Id = $"agent_{Guid.NewGuid():N}",
            ProjectId = projectId,
            Name = name,
            Description = description,
        };
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
            Id = agent.Id,
            State = AgentStore.Serialize(agent),
        });
        await db.SaveChangesAsync();
        return new(projectId, agent);
    }

    private static string Path(string projectId, string? connectionId = null) =>
        $"/api/projects/{projectId}/slack-connections{(connectionId is null ? string.Empty : $"/{connectionId}")}";

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private sealed record SeededAgent(string ProjectId, DomainAgent Agent);
}
