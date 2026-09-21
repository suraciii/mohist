using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using DomainAgent = Mohist.Server.Agent.Domain.Agent;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L1")]
public sealed class SlackManagerApplicationSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackManagerApplicationSpecs(DefaultMohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Legacy_manual_manager_app_route_is_removed()
    {
        var seeded = await SeedAgentAsync(AgentStatus.Active);
        using var response = await _fixture.Client.PostAsJsonAsync(
            ManagerPath(seeded.ProjectId, "/apps"), new { agentId = seeded.Agent.Id });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Legacy_manual_manager_app_route_cannot_create_a_binding()
    {
        var seeded = await SeedAgentAsync(AgentStatus.Active);
        using var response = await _fixture.Client.PostAsJsonAsync(
            ManagerPath(seeded.ProjectId, "/apps"), new { agentId = seeded.Agent.Id });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<SeededAgent> SeedAgentAsync(string status)
    {
        var projectId = $"project_manager_{Guid.NewGuid():N}";
        var agent = new DomainAgent
        {
            Id = $"agent_manager_{Guid.NewGuid():N}",
            ProjectId = projectId,
            Name = "release_helper",
            Description = "Reviews release changes.",
            Status = status,
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
            ProjectId = projectId,
            Name = agent.Name,
            Status = status,
            State = AgentStore.Serialize(agent),
        });
        await db.SaveChangesAsync();
        return new(projectId, agent);
    }

    private static string ManagerPath(string projectId, string suffix = "") =>
        $"/api/projects/{projectId}/slack-manager{suffix}";

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private sealed record SeededAgent(string ProjectId, DomainAgent Agent);
}
