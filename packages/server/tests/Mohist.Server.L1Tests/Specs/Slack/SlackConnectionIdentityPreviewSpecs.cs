using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.L1Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using DomainAgent = Mohist.Server.Agent.Domain.Agent;

namespace Mohist.Server.L1Tests.Specs.Slack;

[Trait("level", "L1")]
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
