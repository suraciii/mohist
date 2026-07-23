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
    public static Task<AgentInfo?> ResolveAsync(
        AgentQuerier querier,
        string projectId,
        string agentRef) =>
        ResolveAsync(
            agentRef,
            id => querier.GetByIdAsync(projectId, id),
            name => querier.GetByNameAsync(projectId, name));

    public static async Task<T?> ResolveAsync<T>(
        string agentRef,
        Func<string, Task<T?>> getById,
        Func<string, Task<T?>> getByName)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(agentRef))
            return null;

        if (agentRef.StartsWith("agent_", StringComparison.Ordinal))
            return await getById(agentRef);

        var byName = await getByName(agentRef);
        if (byName is not null) return byName;

        return await getById(agentRef);
    }
}
