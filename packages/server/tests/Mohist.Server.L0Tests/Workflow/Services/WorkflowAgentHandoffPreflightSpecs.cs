using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.L0Tests.Workflow.Services;

[Collection("MohistDb")]
[Trait("level", "L0")]
public sealed class WorkflowAgentHandoffPreflightSpecs
{
    private readonly MohistDbFixture _fixture;

    public WorkflowAgentHandoffPreflightSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ResolveAgentAsync_ProductionRegistration_UsesScopedSnapshotResolver()
    {
        var preflight = _fixture.Services.GetRequiredService<IWorkflowAgentHandoffPreflight>();

        var result = await preflight.ResolveAgentAsync(
            $"handoff-preflight-project-{Guid.NewGuid():N}",
            $"agent_missing_{Guid.NewGuid():N}");

        Assert.Null(result.Agent);
        Assert.Equal("agent_not_found", result.ErrorCode);
    }

    [Fact]
    public async Task ResolveAgentAsync_NeedsSetupAgent_ReturnsReadinessFailure()
    {
        var projectId = $"handoff-preflight-project-{Guid.NewGuid():N}";
        var agentId = $"handoff-preflight-agent-{Guid.NewGuid():N}";
        var agentName = $"handoff-preflight-name-{Guid.NewGuid():N}";
        await using var scope = _fixture.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var agent = new Mohist.Server.Agent.Domain.Agent
        {
            Id = agentId,
            ProjectId = projectId,
            Name = agentName,
            Description = "handoff preflight needs setup",
            Instructions = "follow the task",
            Skills = [],
            AgentConfig = JsonSerializer.SerializeToElement(new { runtime = "opencode" }),
            Status = AgentStatus.Active,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
        db.Agents.Add(new AgentRow
        {
            Id = GrainKey.Agent(projectId, agentId),
            ProjectId = projectId,
            Name = agentName,
            Status = AgentStatus.Active,
            State = AgentStore.Serialize(agent),
        });
        await db.SaveChangesAsync();

        var preflight = scope.ServiceProvider.GetRequiredService<IWorkflowAgentHandoffPreflight>();
        var result = await preflight.ResolveAgentAsync(projectId, agentName);

        Assert.Null(result.Agent);
        Assert.Equal("agent_not_ready", result.ErrorCode);
        Assert.Contains("not ready", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAgentAsync_ProductionRegistration_ReturnsCanonicalAgentId()
    {
        var projectId = $"handoff-preflight-project-{Guid.NewGuid():N}";
        var agentId = $"handoff-preflight-agent-{Guid.NewGuid():N}";
        var agentName = $"handoff-preflight-name-{Guid.NewGuid():N}";
        await using var scope = _fixture.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var agent = new Mohist.Server.Agent.Domain.Agent
        {
            Id = agentId,
            ProjectId = projectId,
            Name = agentName,
            Description = "handoff preflight spec",
            Instructions = "follow the task",
            Skills = [],
            AgentConfig = JsonSerializer.SerializeToElement(new { runtime = "opencode", model = "openai/gpt-5.4" }),
            Status = AgentStatus.Active,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
        db.Agents.Add(new AgentRow
        {
            Id = GrainKey.Agent(projectId, agentId),
            ProjectId = projectId,
            Name = agentName,
            Status = AgentStatus.Active,
            State = AgentStore.Serialize(agent),
        });
        await db.SaveChangesAsync();

        var preflight = scope.ServiceProvider.GetRequiredService<IWorkflowAgentHandoffPreflight>();
        var result = await preflight.ResolveAgentAsync(projectId, agentName);

        Assert.NotNull(result.Agent);
        Assert.Equal(agentId, result.Agent!.AgentId);
        Assert.Equal("opencode", result.Agent.ExecutionDefinition.Runtime);
    }
}
