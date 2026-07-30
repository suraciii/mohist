using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Services;
using Orleans.Runtime;

namespace Mohist.Server.Agent.Grains;

public sealed class AgentConcurrencyGrain : Grain, IAgentConcurrencyGrain
{
    internal const string ReconciliationReminderName = "agent-concurrency-reconciliation";
    private static readonly TimeSpan ReminderDue = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReminderPeriod = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UnconfirmedPermitRetention = TimeSpan.FromMinutes(1);

    private readonly IPersistentState<AgentConcurrencyState> _state;
    private readonly AgentQuerier _agents;
    private readonly AgentJobQuerier _jobs;
    private readonly IAgentSessionStore _sessions;
    private readonly IGrainFactory _grains;
    private readonly TimeProvider _timeProvider;

    public AgentConcurrencyGrain(
        [PersistentState("agent-concurrency")] IPersistentState<AgentConcurrencyState> state,
        AgentQuerier agents,
        AgentJobQuerier jobs,
        IAgentSessionStore sessions,
        IGrainFactory grains,
        TimeProvider timeProvider)
    {
        _state = state;
        _agents = agents;
        _jobs = jobs;
        _sessions = sessions;
        _grains = grains;
        _timeProvider = timeProvider;
    }

    private string Key => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (!_state.RecordExists)
            await _state.ReadStateAsync();
        await EnsureReminderAsync();
        await ReconcileFromAuthoritativeStateAsync();
    }

    public async Task<AgentConcurrencyAcquireResult> AcquireAsync(
        string projectId,
        string agentId,
        string token,
        string ownerId,
        AgentConcurrencyPermitOwnerKind ownerKind)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token is required.", nameof(token));
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("Owner id is required.", nameof(ownerId));

        var limit = await ReadLimitAsync(projectId, agentId);

        if (limit is null)
        {
            var waiters = DrainWaiters();
            if (waiters.Count > 0)
                await _state.WriteStateAsync();
            foreach (var jobId in waiters)
                _ = NotifyWaiterAsync(jobId);
            return AgentConcurrencyAcquireResult.Granted;
        }

        if (_state.State.ActivePermits.Any(permit => string.Equals(permit.Token, token, StringComparison.Ordinal)))
            return AgentConcurrencyAcquireResult.Granted;
        if (_state.State.Waiters.Any(waiter => string.Equals(waiter.Token, token, StringComparison.Ordinal)))
            return AgentConcurrencyAcquireResult.Waiting;

        if (_state.State.ActivePermits.Count < limit.Value)
        {
            _state.State.ActivePermits.Add(new AgentConcurrencyPermit(
                token,
                ownerId,
                ownerKind,
                _timeProvider.GetUtcNow()));
            await _state.WriteStateAsync();
            return AgentConcurrencyAcquireResult.Granted;
        }

        _state.State.Waiters.Add(new AgentConcurrencyWaiter(token, ownerId, ownerKind));
        await _state.WriteStateAsync();
        return AgentConcurrencyAcquireResult.Waiting;
    }

    public async Task ReleaseAsync(string projectId, string agentId, string token)
    {
        var limit = await ReadLimitAsync(projectId, agentId);
        _state.State.ActivePermits.RemoveAll(permit => string.Equals(permit.Token, token, StringComparison.Ordinal));
        _state.State.Waiters.RemoveAll(waiter => string.Equals(waiter.Token, token, StringComparison.Ordinal));
        var waiters = limit is null
            ? DrainWaiters()
            : await GrantWaitersAsync(projectId, agentId);
        await _state.WriteStateAsync();
        foreach (var jobId in waiters)
            _ = NotifyWaiterAsync(jobId);
    }

    public async Task ReconcileAsync(string projectId, string agentId, IReadOnlySet<string> activeTokens)
    {
        _state.State.ActivePermits.RemoveAll(permit => !activeTokens.Contains(permit.Token));
        var limit = await ReadLimitAsync(projectId, agentId);
        var waiters = limit is null
            ? DrainWaiters()
            : await GrantWaitersAsync(projectId, agentId, limit.Value);
        await _state.WriteStateAsync();
        foreach (var jobId in waiters)
            _ = NotifyWaiterAsync(jobId);
    }

    public Task<int> GetActiveCountAsync() => Task.FromResult(_state.State.ActivePermits.Count);

    public Task<IReadOnlyList<string>> GetActiveTokensAsync() =>
        Task.FromResult<IReadOnlyList<string>>(_state.State.ActivePermits.Select(permit => permit.Token).ToArray());

    public Task<IReadOnlyList<AgentConcurrencyWaiter>> GetWaitersAsync() =>
        Task.FromResult<IReadOnlyList<AgentConcurrencyWaiter>>(_state.State.Waiters.ToArray());

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (string.Equals(reminderName, ReconciliationReminderName, StringComparison.Ordinal))
            await ReconcileFromAuthoritativeStateAsync();
    }

    private async Task<IReadOnlyList<string>> GrantWaitersAsync(
        string projectId,
        string agentId,
        int? knownLimit = null)
    {
        var limit = knownLimit ?? await ReadLimitAsync(projectId, agentId);
        if (limit is null)
            return DrainWaiters();

        var jobsToNotify = new List<string>();
        while (_state.State.Waiters.Count > 0
            && _state.State.ActivePermits.Count < limit.Value)
        {
            var waiter = _state.State.Waiters[0];
            _state.State.Waiters.RemoveAt(0);
            if (_state.State.ActivePermits.Any(permit => string.Equals(permit.Token, waiter.Token, StringComparison.Ordinal)))
                continue;
            _state.State.ActivePermits.Add(new AgentConcurrencyPermit(waiter.Token, waiter.OwnerId, waiter.OwnerKind));
            if (waiter.OwnerKind == AgentConcurrencyPermitOwnerKind.Job)
                jobsToNotify.Add(waiter.OwnerId);
        }
        return jobsToNotify;
    }

    private IReadOnlyList<string> DrainWaiters()
    {
        var jobsToNotify = _state.State.Waiters
            .Where(waiter => waiter.OwnerKind == AgentConcurrencyPermitOwnerKind.Job)
            .Select(waiter => waiter.OwnerId)
            .ToArray();
        _state.State.Waiters.Clear();
        return jobsToNotify;
    }

    private async Task NotifyWaiterAsync(string jobId)
    {
        try
        {
            await _grains.GetGrain<IAgentJobGrain>(jobId).ConcurrencyPermitGrantedAsync();
        }
        catch
        {
        }
    }

    private async Task<int?> ReadLimitAsync(string projectId, string agentId)
    {
        var agent = await _agents.GetByIdAsync(projectId, agentId);
        return agent?.MaxConcurrentRuns;
    }

    private async Task ReconcileFromAuthoritativeStateAsync()
    {
        var parts = Key.Split(':', 2);
        if (parts.Length != 2)
            return;
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var permit in _state.State.ActivePermits)
        {
            if (await IsPermitActiveAsync(parts[0], parts[1], permit)
                || IsAwaitingOwnerPersistence(permit))
                active.Add(permit.Token);
        }
        await ReconcileAsync(parts[0], parts[1], active);
    }

    private async Task<bool> IsPermitActiveAsync(
        string projectId,
        string agentId,
        AgentConcurrencyPermit permit)
    {
        if (permit.OwnerKind == AgentConcurrencyPermitOwnerKind.Job)
            return await _jobs.HoldsConcurrencyPermitAsync(permit.OwnerId, permit.Token);

        var session = await _sessions.LoadAsync(permit.OwnerId);
        if (session is null
            || !string.Equals(session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId), projectId, StringComparison.Ordinal)
            || !string.Equals(session.Metadata.Label(GenericAgentSessionMetadata.AgentId), agentId, StringComparison.Ordinal))
            return false;

        var leases = session.Status.PendingFollowups is { Count: > 0 }
            ? session.Status.PendingFollowups
            : session.Status.PendingFollowup is null ? [] : [session.Status.PendingFollowup];
        return leases.Any(lease => string.Equals(lease.ConcurrencyToken, permit.Token, StringComparison.Ordinal));
    }

    private bool IsAwaitingOwnerPersistence(AgentConcurrencyPermit permit) =>
        permit.GrantedAt is { } grantedAt
        && _timeProvider.GetUtcNow() - grantedAt < UnconfirmedPermitRetention;

    private Task EnsureReminderAsync() => this.RegisterOrUpdateReminder(
        ReconciliationReminderName,
        ReminderDue,
        ReminderPeriod);
}
