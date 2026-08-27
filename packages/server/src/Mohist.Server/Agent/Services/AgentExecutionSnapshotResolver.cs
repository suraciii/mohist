using Mohist.Server.Agent.Domain;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Agent.Services;

public sealed class AgentExecutionSnapshotResolver(
    AgentQuerier agents) : IAgentExecutionSnapshotResolver, IAgentExecutionIdentitySnapshotResolver, IScopedService
{
    public async Task<AgentExecutionDefinition?> ResolveAsync(string projectId, string agentRef)
    {
        var snapshot = await ResolveWithIdentityAsync(projectId, agentRef);
        return snapshot?.ExecutionDefinition;
    }

    public async Task<AgentExecutionIdentitySnapshot?> ResolveWithIdentityAsync(string projectId, string agentRef)
    {
        var agent = await AgentRefResolver.ResolveAsync(agents, projectId, agentRef);
        if (agent is null
            && !agentRef.StartsWith("agent_", StringComparison.Ordinal)
            && BuiltInAgentCatalog.Find(agentRef) is not null)
        {
            agent = BuiltInAgentCatalog.Resolve(agentRef, projectId);
        }
        if (agent is null || agent.Status != AgentStatus.Active)
            return null;
        if (agent.Executability is { } executability
            && AgentExecutabilityStates.IsBlocked(executability.State))
            return null;

        var config = agent.AgentConfig?.Clone();
        if (AgentConfigSchema.Validate(config) is not null)
            return null;

        var allowedSubagents = new List<AllowedSubagentSnapshot>();
        foreach (var allowedId in agent.AllowedSubagentAgentIds ?? [])
        {
            var allowed = await agents.GetByIdAsync(projectId, allowedId);
            if (allowed is not null)
            {
                allowedSubagents.Add(new AllowedSubagentSnapshot(
                    allowed.Id,
                    allowed.Name,
                    allowed.Description));
            }
        }

        var effective = agent.EffectiveExecutionConfig;
        return new AgentExecutionIdentitySnapshot(
            agent.Id,
            new AgentExecutionDefinition(
                Instructions: agent.Instructions,
                Runtime: effective?.Runtime ?? AgentLauncher.ResolveRuntime(config),
                Model: effective?.Model ?? AgentLauncher.ResolveModelAndVariant(config).Model,
                Variant: effective?.Variant ?? AgentLauncher.ResolveModelAndVariant(config).Variant,
                Skills: agent.Skills.ToArray(),
                AllowedSubagents: allowedSubagents.ToArray(),
                ReasoningEffort: AgentLauncher.ResolveReasoningEffort(config)));
    }
}
