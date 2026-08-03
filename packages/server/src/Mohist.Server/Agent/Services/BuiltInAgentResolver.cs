namespace Mohist.Server.Agent.Services;

using Mohist.Server.Infrastructure.Hosting;

public sealed class BuiltInAgentResolver : IScopedService
{
    public Task<AgentInfo?> ResolveAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(
            BuiltInAgentCatalog.Find(name) is null ? null : BuiltInAgentCatalog.Resolve(name));
    }

    public Task<IReadOnlyList<AgentInfo>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AgentInfo>>(
            BuiltInAgentCatalog.Definitions.Select(definition => BuiltInAgentCatalog.Resolve(definition.Name)).ToArray());
    }
}
