using Mohist.Server.Api;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Agent.Services;

public sealed class AgentExecutionSnapshotResolver(
    AgentQuerier agents) : IAgentExecutionSnapshotResolver, IScopedService
{
    public async Task<AgentExecutionSnapshot?> ResolveAsync(string projectId, string agentRef)
    {
        var agent = await AgentRefResolver.ResolveAsync(agents, projectId, agentRef);
        if (agent is null || agent.Status != AgentStatus.Active)
            return null;

        var config = agent.AgentConfig?.Clone();
        if (AgentConfigSchema.Validate(config) is not null)
            return null;
        return new AgentExecutionSnapshot(agent.Instructions, config);
    }
}
