using Mohist.Server.Agent.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Shared <c>agent_*</c> id-else-name resolution for Agent profile references
/// within a project. The resolve rule: if <paramref name="agentRef"/> starts
/// with <c>agent_</c>, treat it as an id (<see cref="AgentQuerier.GetByIdAsync"/>);
/// otherwise try name first (<see cref="AgentQuerier.GetByNameAsync"/>), then
/// id as a fallback. Returns <c>null</c> when the ref does not resolve.
/// </summary>
public static class AgentRefResolver
{
    public static async Task<AgentInfo?> ResolveAsync(
        AgentQuerier querier,
        string projectId,
        string agentRef)
    {
        if (string.IsNullOrWhiteSpace(agentRef))
            return null;

        if (agentRef.StartsWith("agent_", StringComparison.Ordinal))
            return await querier.GetByIdAsync(projectId, agentRef);

        var byName = await querier.GetByNameAsync(projectId, agentRef);
        if (byName is not null) return byName;

        return await querier.GetByIdAsync(projectId, agentRef);
    }
}
