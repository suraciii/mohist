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
using Mohist.Server.SpecTests.Support;
using Xunit;
using DomainAgent = Mohist.Server.Agent.Domain.Agent;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("MohistIntegration")]
public sealed class SlackManagerApplicationSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackManagerApplicationSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Create_selects_active_agent_persists_manager_records_and_is_idempotent()
    {
        var seeded = await SeedAgentAsync(AgentStatus.Active);
        var request = new
        {
            agentId = seeded.Agent.Id,
            workspaceTeamId = "T_MANAGER_CREATE",
            managerExternalId = "manager-1",
            accessPolicy = AccessPolicyKind.Allowlist,
            ownerSlackUserId = "U_OWNER",
            transportKind = "socket",
        };

        using var firstResponse = await _fixture.Client.PostAsJsonAsync(ManagerPath(seeded.ProjectId, "/apps"), request);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var first = await ReadDataAsync(firstResponse);
        var connectionId = first.GetProperty("connection").GetProperty("id").GetString()!;
        var childId = first.GetProperty("managedApp").GetProperty("id").GetString()!;
        Assert.Equal("release_helper", first.GetProperty("preview").GetProperty("botName").GetString());
        Assert.Equal("not_created", first.GetProperty("managedApp").GetProperty("appLifecycle").GetString());
        Assert.Equal("create_child_app", first.GetProperty("managedApp").GetProperty("nextAction").GetString());

        using var secondResponse = await _fixture.Client.PostAsJsonAsync(ManagerPath(seeded.ProjectId, "/apps"), request);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = await ReadDataAsync(secondResponse);
        Assert.False(second.GetProperty("created").GetBoolean());
        Assert.Equal(connectionId, second.GetProperty("connection").GetProperty("id").GetString());
        Assert.Equal(childId, second.GetProperty("managedApp").GetProperty("id").GetString());

        using var listResponse = await _fixture.Client.GetAsync(
            $"{ManagerPath(seeded.ProjectId, "/agents")}?workspaceTeamId=T_MANAGER_CREATE");
        listResponse.EnsureSuccessStatusCode();
        var list = await ReadDataAsync(listResponse);
        var option = Assert.Single(list.EnumerateArray());
        Assert.Equal(seeded.Agent.Id, option.GetProperty("agentId").GetString());
        Assert.Equal(childId, option.GetProperty("managedApp").GetProperty("id").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Single(await db.SlackWorkspaceEnrollments.Where(row => row.WorkspaceTeamId == "T_MANAGER_CREATE").ToListAsync());
        var connection = await db.AgentConnections.SingleAsync(row => row.Id == connectionId);
        Assert.Equal(AccessPolicyKind.Allowlist, connection.AccessPolicy);
        Assert.Equal("T_MANAGER_CREATE", connection.WorkspaceTeamId);
        var child = await db.ManagedSlackChildApps.SingleAsync(row => row.Id == childId);
        Assert.Equal(connectionId, child.AgentConnectionId);
        Assert.NotEmpty(child.DesiredManifestHash);
        Assert.Equal(seeded.Agent.Id, (await db.Agents.SingleAsync(row => row.Id == seeded.Agent.Id)).Id);
    }

    [Fact]
    public async Task Archived_agent_is_not_a_manager_candidate_or_create_target()
    {
        var seeded = await SeedAgentAsync(AgentStatus.Archived);
        using var response = await _fixture.Client.PostAsJsonAsync(ManagerPath(seeded.ProjectId, "/apps"), new
        {
            agentId = seeded.Agent.Id,
            workspaceTeamId = "T_MANAGER_ARCHIVED",
            managerExternalId = "manager-1",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("agent_archived", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Remove_binding_keeps_child_facts_and_permanent_delete_requires_explicit_confirmation()
    {
        var seeded = await SeedAgentAsync(AgentStatus.Active);
        using var createResponse = await _fixture.Client.PostAsJsonAsync(ManagerPath(seeded.ProjectId, "/apps"), new
        {
            agentId = seeded.Agent.Id,
            workspaceTeamId = "T_MANAGER_REMOVE",
            managerExternalId = "manager-1",
        });
        var created = await ReadDataAsync(createResponse);
        var connectionId = created.GetProperty("connection").GetProperty("id").GetString()!;
        var childId = created.GetProperty("managedApp").GetProperty("id").GetString()!;

        using var removeResponse = await _fixture.Client.PostAsJsonAsync(
            ManagerPath(seeded.ProjectId, $"/connections/{connectionId}/remove-binding"), new { });
        Assert.Equal(HttpStatusCode.OK, removeResponse.StatusCode);
        var removed = await ReadDataAsync(removeResponse);
        Assert.True(removed.GetProperty("removedBinding").GetBoolean());
        Assert.Equal(childId, removed.GetProperty("managedApp").GetProperty("id").GetString());

        using var unconfirmedResponse = await _fixture.Client.PostAsJsonAsync(
            ManagerPath(seeded.ProjectId, $"/connections/{connectionId}/permanent-delete"), new { });
        Assert.Equal(HttpStatusCode.Conflict, unconfirmedResponse.StatusCode);
        using (var error = JsonDocument.Parse(await unconfirmedResponse.Content.ReadAsStringAsync()))
            Assert.Equal("confirmation_required", error.RootElement.GetProperty("code").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.NotNull(await db.AgentConnections.SingleAsync(row => row.Id == connectionId && row.DeletedAt != null));
        Assert.NotNull(await db.ManagedSlackChildApps.SingleAsync(row => row.Id == childId && row.DeletedAt == null));
        Assert.NotNull(await db.Agents.SingleAsync(row => row.Id == seeded.Agent.Id));
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
