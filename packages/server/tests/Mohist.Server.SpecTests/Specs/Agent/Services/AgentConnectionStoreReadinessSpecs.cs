using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Services;

public sealed partial class AgentConnectionStoreSpecs
{
    [Fact]
    public async Task MissingRuntimeUsesCanonicalDefaultAcrossConnectionAndSubscriptionReadiness()
    {
        using var configDocument = JsonDocument.Parse("{\"model\":\"provider/model\"}");
        await SeedReadinessAgentAsync(
            "proj-runtime-default",
            "agent-runtime-default",
            configDocument.RootElement.Clone());

        var created = await _store.CreateAsync(NewConnection("proj-runtime-default", "agent-runtime-default", "team-1"));
        var displayed = await _store.GetAsync("proj-runtime-default", created.Id);
        var agent = await new AgentQuerier(_factory).GetByIdAsync("proj-runtime-default", "agent-runtime-default");

        Assert.NotNull(displayed);
        Assert.Equal(AgentReadinessKind.Ready, displayed!.AgentReadiness);
        Assert.NotNull(agent);
        Assert.Equal(AgentReadinessKind.Ready, AgentReadinessDeriver.Derive(agent!.AgentConfig));
        var subscriptionReadiness = AgentReadinessService.Evaluate(agent, null);
        Assert.Equal(AgentReadinessConclusions.Unknown, subscriptionReadiness.Conclusion);
        Assert.NotEqual(AgentReadinessConclusions.NeedsSetup, subscriptionReadiness.Conclusion);
        Assert.Equal("opencode", AgentLauncher.ResolveRuntime(agent!.AgentConfig));
    }

    private async Task SeedReadinessAgentAsync(string projectId, string agentId, JsonElement agentConfig)
    {
        await using var db = _factory.CreateDbContext();
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = projectId,
                Name = agentId,
                Instructions = "Do the work",
                AgentConfig = agentConfig,
                Status = AgentStatus.Active,
            }, JSON.Options),
        });
        await db.SaveChangesAsync();
    }
}
