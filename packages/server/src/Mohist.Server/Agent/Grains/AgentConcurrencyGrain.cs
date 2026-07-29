using Mohist.Server.Agent.Services;
using Orleans.Runtime;

namespace Mohist.Server.Agent.Grains;

public sealed class AgentConcurrencyGrain : Grain, IAgentConcurrencyGrain
{
    internal const string ReconciliationReminderName = "agent-concurrency-reconciliation";
    private static readonly TimeSpan ReminderDue = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReminderPeriod = TimeSpan.FromSeconds(30);

    private readonly IPersistentState<AgentConcurrencyState> _state;
    private readonly AgentQuerier _agents;
    private readonly IGrainFactory _grains;

    public AgentConcurrencyGrain(
        [PersistentState("agent-concurrency")] IPersistentState<AgentConcurrencyState> state,
        AgentQuerier agents,
        IGrainFactory grains)
    {
        _state = state;
        _agents = agents;
        _grains = grains;
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
        string jobId)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token is required.", nameof(token));
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("Job id is required.", nameof(jobId));

        var limit = await ReadLimitAsync(projectId, agentId);

        // Issue-520 T-001 D3 / T-002: a null MaxConcurrentRuns means
        // there is no per-agent bound (agent-concurrency spec, "Unset
        // limit imposes no per-agent bound"). The gate returns Granted
        // immediately and does not track the token — both because no
        // bound applies and because there is no Release that needs to
        // be matched. The caller (AgentJobGrain, AgentSessionGrain) still
        // records its own "permit held" view so the standard
        // acquire/release contract is symmetric.
        if (limit is null)
            return AgentConcurrencyAcquireResult.Granted;

        if (_state.State.ActiveTokens.Contains(token, StringComparer.Ordinal))
            return AgentConcurrencyAcquireResult.Granted;
        if (_state.State.Waiters.Any(waiter => string.Equals(waiter.Token, token, StringComparison.Ordinal)))
            return AgentConcurrencyAcquireResult.Waiting;

        if (_state.State.ActiveTokens.Count < limit.Value)
        {
            _state.State.ActiveTokens.Add(token);
            await _state.WriteStateAsync();
            return AgentConcurrencyAcquireResult.Granted;
        }

        _state.State.Waiters.Add(new AgentConcurrencyWaiter(token, jobId));
        await _state.WriteStateAsync();
        return AgentConcurrencyAcquireResult.Waiting;
    }

    public async Task ReleaseAsync(string projectId, string agentId, string token)
    {
        var limit = await ReadLimitAsync(projectId, agentId);
        // Same null-limit shortcut as AcquireAsync: nothing was tracked,
        // nothing to release, nothing to grant.
        if (limit is null)
            return;

        _state.State.ActiveTokens.RemoveAll(value => string.Equals(value, token, StringComparison.Ordinal));
        _state.State.Waiters.RemoveAll(waiter => string.Equals(waiter.Token, token, StringComparison.Ordinal));
        await GrantWaitersAsync(projectId, agentId);
        await _state.WriteStateAsync();
    }

    public async Task ReconcileAsync(string projectId, string agentId, IReadOnlySet<string> activeTokens)
    {
        _state.State.ActiveTokens.RemoveAll(token => !activeTokens.Contains(token));
        await GrantWaitersAsync(projectId, agentId);
        await _state.WriteStateAsync();
    }

    public Task<int> GetActiveCountAsync() => Task.FromResult(_state.State.ActiveTokens.Count);

    public Task<IReadOnlyList<string>> GetActiveTokensAsync() =>
        Task.FromResult<IReadOnlyList<string>>(_state.State.ActiveTokens.ToArray());

    public Task<IReadOnlyList<AgentConcurrencyWaiter>> GetWaitersAsync() =>
        Task.FromResult<IReadOnlyList<AgentConcurrencyWaiter>>(_state.State.Waiters.ToArray());

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (string.Equals(reminderName, ReconciliationReminderName, StringComparison.Ordinal))
            await ReconcileFromAuthoritativeStateAsync();
    }

    private async Task GrantWaitersAsync(string projectId, string agentId)
    {
        var limit = await ReadLimitAsync(projectId, agentId);
        while (_state.State.Waiters.Count > 0
            && (limit is null || _state.State.ActiveTokens.Count < limit.Value))
        {
            var waiter = _state.State.Waiters[0];
            _state.State.Waiters.RemoveAt(0);
            if (_state.State.ActiveTokens.Contains(waiter.Token, StringComparer.Ordinal))
                continue;
            _state.State.ActiveTokens.Add(waiter.Token);
            _ = NotifyWaiterAsync(waiter.JobId);
        }
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
        await ReconcileAsync(parts[0], parts[1], active);
    }

    private Task EnsureReminderAsync() => this.RegisterOrUpdateReminder(
        ReconciliationReminderName,
        ReminderDue,
        ReminderPeriod);
}
