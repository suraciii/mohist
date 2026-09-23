using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    public async Task<AgentSessionStopClaim?> GetStopClaimAsync() =>
        (await GetRequiredAsync()).Status.PendingStop;

    public async Task<AgentSessionStopClaim?> GetStopClaimAsync(string turnId, string operationId) =>
        (await GetRequiredAsync()).FindStopClaim(turnId, operationId);
}
