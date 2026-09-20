using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using DomainAgent = Mohist.Server.Agent.Domain.Agent;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L1")]
public sealed class SlackConnectionIdentityPreviewSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackConnectionIdentityPreviewSpecs(DefaultMohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Legacy_empty_connection_create_route_is_removed()
    {
        var seeded = await SeedAgentAsync("release_helper", "Reviews release changes.");

        using var response = await _fixture.Client.PostAsJsonAsync(Path(seeded.ProjectId), new
        {
            agentId = seeded.Agent.Id,
            workspaceTeamId = "T_FORGED",
            appId = "A_FORGED",
            botUserId = "U_FORGED",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
