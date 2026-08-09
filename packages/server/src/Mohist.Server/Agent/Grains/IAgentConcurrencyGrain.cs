namespace Mohist.Server.Agent.Grains;

public enum AgentConcurrencyAcquireResult
{
    Granted,
    Waiting,
}

[GenerateSerializer]
public enum AgentConcurrencyPermitStatus
{
    DispatchPending,
    Dispatched,
    Executing,
    Terminal,
    Cancelled,
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
    [property: Id(3)] DateTimeOffset? GrantedAt = null,
    [property: Id(4)] string? PermitId = null,
    [property: Id(5)] string? DispatchId = null,
    [property: Id(6)] long Generation = 0,
    [property: Id(7)] AgentConcurrencyPermitStatus Status = AgentConcurrencyPermitStatus.DispatchPending);

[GenerateSerializer]
public sealed record AgentConcurrencyWaiter(
    [property: Id(0)] string Token,
    [property: Id(1)] string OwnerId,
    [property: Id(2)] AgentConcurrencyPermitOwnerKind OwnerKind,
    [property: Id(3)] string? WaiterId = null,
    [property: Id(4)] string? DispatchId = null,
    [property: Id(5)] long Generation = 0,
    [property: Id(6)] string WaitingReason = "capacity-full");

[GenerateSerializer]
public sealed record AgentConcurrencyPendingNotification(
    [property: Id(0)] string WaiterId,
    [property: Id(1)] string Token,
    [property: Id(2)] string OwnerId,
    [property: Id(3)] AgentConcurrencyPermitOwnerKind OwnerKind,
    [property: Id(4)] string PermitId,
    [property: Id(5)] string DispatchId,
    [property: Id(6)] long Generation,
    [property: Id(7)] int Attempts = 0,
    [property: Id(8)] DateTimeOffset? LastAttemptAt = null);

[GenerateSerializer]
public sealed class AgentConcurrencyState
{
    [Id(0)] public List<AgentConcurrencyPermit> ActivePermits { get; set; } = [];
    [Id(1)] public List<AgentConcurrencyWaiter> Waiters { get; set; } = [];
    [Id(2)] public List<AgentConcurrencyPendingNotification> PendingNotifications { get; set; } = [];
    [Id(3)] public long NextGeneration { get; set; }
}

[GenerateSerializer]
public sealed record AgentConcurrencySnapshot(
    [property: Id(0)] IReadOnlyList<AgentConcurrencyPermit> ActivePermits,
    [property: Id(1)] IReadOnlyList<AgentConcurrencyWaiter> Waiters,
    [property: Id(2)] IReadOnlyList<AgentConcurrencyPendingNotification> PendingNotifications);

public interface IAgentConcurrencyGrain : IGrainWithStringKey, IRemindable
{
    Task<AgentConcurrencyAcquireResult> AcquireAsync(
        string projectId,
        string agentId,
        string token,
        string ownerId,
        AgentConcurrencyPermitOwnerKind ownerKind,
        string? dispatchId = null);
    Task ReleaseAsync(string projectId, string agentId, string token, string? permitId = null, long? generation = null, string? waiterId = null);
    Task<AgentConcurrencyPermit?> GetPermitAsync(string token);
    Task<AgentConcurrencySnapshot> GetSnapshotAsync();
    Task ConfirmDispatchPendingAsync(string projectId, string agentId, string token, string permitId, string dispatchId);
    Task MarkDispatchedAsync(string projectId, string agentId, string token, string permitId, string dispatchId);
    Task MarkExecutingAsync(string projectId, string agentId, string token, string permitId, string dispatchId);
    Task MarkTerminalAsync(string projectId, string agentId, string token, string permitId, string dispatchId, bool cancelled);
    Task ReconcileAsync(string projectId, string agentId, IReadOnlySet<string> activeTokens);
    Task<int> GetActiveCountAsync();
    Task<IReadOnlyList<string>> GetActiveTokensAsync();
    Task<IReadOnlyList<AgentConcurrencyWaiter>> GetWaitersAsync();
}
