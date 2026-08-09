using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Grains;
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
    private readonly ILogger<AgentConcurrencyGrain> _log;

    public AgentConcurrencyGrain(
        [PersistentState("agent-concurrency")] IPersistentState<AgentConcurrencyState> state,
        AgentQuerier agents,
        AgentJobQuerier jobs,
        IAgentSessionStore sessions,
        IGrainFactory grains,
        TimeProvider timeProvider,
        ILogger<AgentConcurrencyGrain> log)
    {
        _state = state;
        _agents = agents;
        _jobs = jobs;
        _sessions = sessions;
        _grains = grains;
        _timeProvider = timeProvider;
        _log = log;
    }

    private string Key => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (!_state.RecordExists)
            await _state.ReadStateAsync();

        await EnsureReminderAsync();
        await ReconcileFromAuthoritativeStateAsync();
        await ProcessPendingNotificationsAsync();
    }

    public async Task<AgentConcurrencyAcquireResult> AcquireAsync(
        string projectId,
        string agentId,
        string token,
        string ownerId,
        AgentConcurrencyPermitOwnerKind ownerKind,
        string? dispatchId = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token is required.", nameof(token));
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("Owner id is required.", nameof(ownerId));

        var limit = await ReadLimitAsync(projectId, agentId);
        var existingPermit = _state.State.ActivePermits.FirstOrDefault(permit =>
            string.Equals(permit.Token, token, StringComparison.Ordinal));
        if (existingPermit is not null)
            return AgentConcurrencyAcquireResult.Granted;

        if (_state.State.Waiters.Any(waiter => string.Equals(waiter.Token, token, StringComparison.Ordinal)))
            return AgentConcurrencyAcquireResult.Waiting;

        if (limit is null)
        {
            var waiters = DrainWaiters();
            if (waiters.Count > 0)
            {
                await _state.WriteStateAsync();
                await ProcessPendingNotificationsAsync();
            }

            return AgentConcurrencyAcquireResult.Granted;
        }

        var stableDispatchId = string.IsNullOrWhiteSpace(dispatchId)
            ? $"{ownerKind}:{ownerId}:{token}"
            : dispatchId;
        var generation = ++_state.State.NextGeneration;
        if (_state.State.ActivePermits.Count < limit.Value)
        {
            var permit = CreatePermit(
                token,
                ownerId,
                ownerKind,
                stableDispatchId,
                generation);
            _state.State.ActivePermits.Add(permit);
            await _state.WriteStateAsync();
            return AgentConcurrencyAcquireResult.Granted;
        }

        _state.State.Waiters.Add(new AgentConcurrencyWaiter(
            token,
            ownerId,
            ownerKind,
            WaiterId: CreateWaiterId(generation),
            DispatchId: stableDispatchId,
            Generation: generation));
        await _state.WriteStateAsync();
        return AgentConcurrencyAcquireResult.Waiting;
    }

    public async Task ReleaseAsync(
        string projectId,
        string agentId,
        string token,
        string? permitId = null,
        long? generation = null,
        string? waiterId = null)
    {
        var removed = false;
        for (var index = _state.State.ActivePermits.Count - 1; index >= 0; index--)
        {
            var permit = _state.State.ActivePermits[index];
            if (!string.Equals(permit.Token, token, StringComparison.Ordinal)
                || (permitId is not null && !string.Equals(permit.PermitId, permitId, StringComparison.Ordinal))
                || (generation is not null && permit.Generation != generation.Value))
                continue;

            _state.State.ActivePermits.RemoveAt(index);
            _state.State.PendingNotifications.RemoveAll(notification =>
                string.Equals(notification.Token, token, StringComparison.Ordinal)
                && string.Equals(notification.PermitId, permit.PermitId, StringComparison.Ordinal)
                && notification.Generation == permit.Generation);
            removed = true;
        }

        for (var index = _state.State.Waiters.Count - 1; index >= 0; index--)
        {
            var waiter = _state.State.Waiters[index];
            if (!string.Equals(waiter.Token, token, StringComparison.Ordinal)
                || (waiterId is not null && !string.Equals(waiter.WaiterId, waiterId, StringComparison.Ordinal))
                || (generation is not null && waiter.Generation != generation.Value)
                || (permitId is not null && waiterId is null && generation is null))
                continue;

            _state.State.Waiters.RemoveAt(index);
            removed = true;
        }

        if (!removed)
            return;

        var limit = await ReadLimitAsync(projectId, agentId);
        var granted = limit is null
            ? DrainWaiters()
            : await GrantWaitersAsync(projectId, agentId, limit.Value);
        await _state.WriteStateAsync();
        await ProcessPendingNotificationsAsync();
        _ = granted;
    }

    public async Task ReconcileAsync(string projectId, string agentId, IReadOnlySet<string> activeTokens)
    {
        _state.State.ActivePermits.RemoveAll(permit =>
            !activeTokens.Contains(permit.Token)
            && !IsAwaitingOwnerPersistence(permit));

        var limit = await ReadLimitAsync(projectId, agentId);
        if (limit is null)
            DrainWaiters();
        else
            await GrantWaitersAsync(projectId, agentId, limit.Value);

        await _state.WriteStateAsync();
        await ProcessPendingNotificationsAsync();
    }

    public Task<int> GetActiveCountAsync() =>
        Task.FromResult(_state.State.ActivePermits.Count);

    public Task<IReadOnlyList<string>> GetActiveTokensAsync() =>
        Task.FromResult<IReadOnlyList<string>>(
            _state.State.ActivePermits.Select(permit => permit.Token).ToArray());

    public Task<IReadOnlyList<AgentConcurrencyWaiter>> GetWaitersAsync() =>
        Task.FromResult<IReadOnlyList<AgentConcurrencyWaiter>>(
            _state.State.Waiters.ToArray());

    public Task<AgentConcurrencyPermit?> GetPermitAsync(string token) =>
        Task.FromResult(_state.State.ActivePermits.FirstOrDefault(permit =>
            string.Equals(permit.Token, token, StringComparison.Ordinal)));

    public Task<AgentConcurrencySnapshot> GetSnapshotAsync() =>
        Task.FromResult(new AgentConcurrencySnapshot(
            _state.State.ActivePermits.ToArray(),
            _state.State.Waiters.ToArray(),
            _state.State.PendingNotifications.ToArray()));

    public Task ConfirmDispatchPendingAsync(
        string projectId,
        string agentId,
        string token,
        string permitId,
        string dispatchId) =>
        UpdatePermitStatusAsync(token, permitId, dispatchId, AgentConcurrencyPermitStatus.DispatchPending);

    public Task MarkDispatchedAsync(
        string projectId,
        string agentId,
        string token,
        string permitId,
        string dispatchId) =>
        UpdatePermitStatusAsync(token, permitId, dispatchId, AgentConcurrencyPermitStatus.Dispatched);

    public Task MarkExecutingAsync(
        string projectId,
        string agentId,
        string token,
        string permitId,
        string dispatchId) =>
        UpdatePermitStatusAsync(token, permitId, dispatchId, AgentConcurrencyPermitStatus.Executing);

    public Task MarkTerminalAsync(
        string projectId,
        string agentId,
        string token,
        string permitId,
        string dispatchId,
        bool cancelled) =>
        UpdatePermitStatusAsync(
            token,
            permitId,
            dispatchId,
            cancelled
                ? AgentConcurrencyPermitStatus.Cancelled
                : AgentConcurrencyPermitStatus.Terminal);

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, ReconciliationReminderName, StringComparison.Ordinal))
            return;

        await ReconcileFromAuthoritativeStateAsync();
        await ProcessPendingNotificationsAsync();
    }

    private async Task<IReadOnlyList<AgentConcurrencyWaiter>> GrantWaitersAsync(
        string projectId,
        string agentId,
        int? knownLimit = null)
    {
        var limit = knownLimit ?? await ReadLimitAsync(projectId, agentId);
        if (limit is null)
            return DrainWaiters();

        var granted = new List<AgentConcurrencyWaiter>();
        while (_state.State.Waiters.Count > 0
            && _state.State.ActivePermits.Count < limit.Value)
        {
            var waiter = _state.State.Waiters[0];
            _state.State.Waiters.RemoveAt(0);
            if (_state.State.ActivePermits.Any(permit =>
                    string.Equals(permit.Token, waiter.Token, StringComparison.Ordinal)))
                continue;

            var generation = waiter.Generation == 0
                ? ++_state.State.NextGeneration
                : waiter.Generation;
            var permit = CreatePermit(
                waiter.Token,
                waiter.OwnerId,
                waiter.OwnerKind,
                waiter.DispatchId ?? $"{waiter.OwnerKind}:{waiter.OwnerId}:{waiter.Token}",
                generation);
            _state.State.ActivePermits.Add(permit);
            _state.State.PendingNotifications.Add(new AgentConcurrencyPendingNotification(
                waiter.WaiterId ?? CreateWaiterId(generation),
                waiter.Token,
                waiter.OwnerId,
                waiter.OwnerKind,
                permit.PermitId!,
                permit.DispatchId!,
                permit.Generation));
            granted.Add(waiter);
        }

        return granted;
    }

    private AgentConcurrencyPermit CreatePermit(
        string token,
        string ownerId,
        AgentConcurrencyPermitOwnerKind ownerKind,
        string dispatchId,
        long generation) =>
        new(
            token,
            ownerId,
            ownerKind,
            _timeProvider.GetUtcNow(),
            PermitId: $"{Key}:permit:{generation}",
            DispatchId: dispatchId,
            Generation: generation,
            Status: AgentConcurrencyPermitStatus.DispatchPending);

    private string CreateWaiterId(long generation) => $"{Key}:waiter:{generation}";

    private IReadOnlyList<AgentConcurrencyWaiter> DrainWaiters()
    {
        var waiters = _state.State.Waiters.ToArray();
        _state.State.Waiters.Clear();
        return waiters;
    }

    private async Task ProcessPendingNotificationsAsync()
    {
        foreach (var pending in _state.State.PendingNotifications.ToArray())
        {
            try
            {
                if (pending.OwnerKind == AgentConcurrencyPermitOwnerKind.Job)
                {
                    await _grains.GetGrain<IAgentJobGrain>(pending.OwnerId)
                        .ConcurrencyPermitGrantedAsync(pending.Token, pending.PermitId, pending.DispatchId);
                }
                else
                {
                    await _grains.GetGrain<IAgentSessionGrain>(pending.OwnerId)
                        .ConcurrencyPermitGrantedAsync(pending.Token, pending.PermitId, pending.DispatchId);
                }

                _state.State.PendingNotifications.RemoveAll(notification =>
                    string.Equals(notification.WaiterId, pending.WaiterId, StringComparison.Ordinal)
                    && notification.Generation == pending.Generation
                    && string.Equals(notification.PermitId, pending.PermitId, StringComparison.Ordinal));
                await _state.WriteStateAsync();
            }
            catch (Exception ex)
            {
                var current = _state.State.PendingNotifications.FirstOrDefault(notification =>
                    string.Equals(notification.WaiterId, pending.WaiterId, StringComparison.Ordinal)
                    && notification.Generation == pending.Generation);
                if (current is null)
                    continue;

                _state.State.PendingNotifications.Remove(current);
                _state.State.PendingNotifications.Add(current with
                {
                    Attempts = current.Attempts + 1,
                    LastAttemptAt = _timeProvider.GetUtcNow(),
                });
                _log.LogWarning(
                    ex,
                    "Agent concurrency permit grant notification failed for {OwnerKind} {OwnerId}, permit {PermitId}; durable retry retained",
                    pending.OwnerKind,
                    pending.OwnerId,
                    pending.PermitId);
                await _state.WriteStateAsync();
            }
        }
    }

    private async Task UpdatePermitStatusAsync(
        string token,
        string permitId,
        string dispatchId,
        AgentConcurrencyPermitStatus status)
    {
        var index = _state.State.ActivePermits.FindIndex(permit =>
            string.Equals(permit.Token, token, StringComparison.Ordinal)
            && string.Equals(permit.PermitId, permitId, StringComparison.Ordinal)
            && string.Equals(permit.DispatchId, dispatchId, StringComparison.Ordinal));
        if (index < 0)
            return;

        _state.State.ActivePermits[index] = _state.State.ActivePermits[index] with { Status = status };
        await _state.WriteStateAsync();
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
        permit.Status == AgentConcurrencyPermitStatus.DispatchPending
        && permit.GrantedAt is { } grantedAt
        && _timeProvider.GetUtcNow() - grantedAt < UnconfirmedPermitRetention;

    private Task EnsureReminderAsync() => this.RegisterOrUpdateReminder(
        ReconciliationReminderName,
        ReminderDue,
        ReminderPeriod);
}
