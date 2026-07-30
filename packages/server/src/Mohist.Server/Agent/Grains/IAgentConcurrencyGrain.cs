namespace Mohist.Server.Agent.Grains;

public enum AgentConcurrencyAcquireResult
{
    Granted,
    Waiting,
}

[GenerateSerializer]
public enum AgentConcurrencyPermitOwnerKind
{
    Job,
    Followup,
}

[GenerateSerializer]
public sealed record AgentConcurrencyPermit(
    [property: Id(0)] string Token,
    [property: Id(1)] string OwnerId,
    [property: Id(2)] AgentConcurrencyPermitOwnerKind OwnerKind,
    [property: Id(3)] DateTimeOffset? GrantedAt = null);

[GenerateSerializer]
public sealed record AgentConcurrencyWaiter(
    [property: Id(0)] string Token,
    [property: Id(1)] string OwnerId,
    [property: Id(2)] AgentConcurrencyPermitOwnerKind OwnerKind);

[GenerateSerializer]
public sealed class AgentConcurrencyState
{
    [Id(0)] public List<AgentConcurrencyPermit> ActivePermits { get; set; } = [];
    [Id(1)] public List<AgentConcurrencyWaiter> Waiters { get; set; } = [];
}

public interface IAgentConcurrencyGrain : IGrainWithStringKey, IRemindable
{
    Task<AgentConcurrencyAcquireResult> AcquireAsync(
        string projectId,
        string agentId,
        string token,
        string ownerId,
        AgentConcurrencyPermitOwnerKind ownerKind);
    Task ReleaseAsync(string projectId, string agentId, string token);
    Task ReconcileAsync(string projectId, string agentId, IReadOnlySet<string> activeTokens);
    Task<int> GetActiveCountAsync();
    Task<IReadOnlyList<string>> GetActiveTokensAsync();
    Task<IReadOnlyList<AgentConcurrencyWaiter>> GetWaitersAsync();
}
