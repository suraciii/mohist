using Mohist.Server.Agent.Domain;

namespace Mohist.Server.Agent.Services;

public interface ISlackAgentAppBindingPort
{
    Task<AgentConnection?> BindSlackIdentityAsync(
        string projectId,
        string id,
        string workspaceTeamId,
        string appId,
        string botUserId,
        string? botName,
        CancellationToken ct = default,
        string? claimToken = null);
}
