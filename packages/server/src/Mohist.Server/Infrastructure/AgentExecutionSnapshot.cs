using System.Text.Json;

namespace Mohist.Server.Infrastructure;

public interface IAgentExecutionSnapshotResolver
{
    Task<AgentExecutionSnapshot?> ResolveAsync(string projectId, string agentRef);
}

public sealed record AgentExecutionSnapshot(
    string Instructions,
    JsonElement? AgentConfig);
