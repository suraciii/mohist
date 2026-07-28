using Mohist.Server.Agent.Domain;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Agent.Services;

public sealed class AgentExecutionSnapshotResolver(
    AgentQuerier agents) : IAgentExecutionSnapshotResolver, IScopedService
{
    public async Task<AgentExecutionDefinition?> ResolveAsync(string projectId, string agentRef)
    {
        var agent = await AgentRefResolver.ResolveAsync(agents, projectId, agentRef);
        if (agent is null || agent.Status != AgentStatus.Active)
            return null;

        var config = agent.AgentConfig?.Clone();
        if (AgentConfigSchema.Validate(config) is not null)
            return null;

        return new AgentExecutionDefinition(
            Instructions: agent.Instructions,
            Runtime: AgentLauncher.ResolveRuntime(config),
            Model: AgentLauncher.ResolveModelAndVariant(config).Model,
            Variant: AgentLauncher.ResolveModelAndVariant(config).Variant,
            Skills: agent.Skills.ToArray());
    }
}
