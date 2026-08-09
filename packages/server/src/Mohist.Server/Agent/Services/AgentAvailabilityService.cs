using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Services;

namespace Mohist.Server.Agent.Services;

public static class AgentAvailabilityWaitReasons
{
    public const string NoOnlineRunner = "no-online-runner";
    public const string CapacityFull = "capacity-full";
    public const string ConcurrencyLimit = "concurrency-limit";
    public const string DispatchPending = "dispatch-pending";
}

public sealed record AgentAvailabilityResult(
    bool CanStartNow,
    string? WaitingReason,
    int ActiveRuns,
    int? MaxConcurrentRuns,
    RunnerCapacityView Capacity,
    DateTimeOffset ObservedAt);

public sealed record AgentWaitingWork(
    string JobId,
    string Status,
    string WaitingReason,
    string? SubmittedAt);

public sealed record AgentAvailabilityListEntry(
    string AgentId,
    bool CanStartNow,
    string? WaitingReason,
    int ActiveRuns,
    int? MaxConcurrentRuns,
    RunnerCapacityView Capacity,
    int QueuedCount);

public sealed class AgentAvailabilityService : IScopedService
{
    private readonly IRunnerStatusSource _runnerStatus;
    private readonly IGrainFactory _grains;
    private readonly AgentJobQuerier _jobs;
    private readonly TimeProvider _timeProvider;

    public AgentAvailabilityService(
        IRunnerStatusSource runnerStatus,
        IGrainFactory grains,
        AgentJobQuerier jobs,
        TimeProvider timeProvider)
    {
        _runnerStatus = runnerStatus;
        _grains = grains;
        _jobs = jobs;
        _timeProvider = timeProvider;
    }

    public async Task<AgentAvailabilityResult?> GetAsync(
        string projectId,
        AgentInfo agent,
        CancellationToken ct = default)
    {
        var runners = await _runnerStatus.GetOnlineRunnersAsync(projectId, ct);
        var capacity = SumCapacity(runners);
        var snapshot = await _grains
            .GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agent.Id))
            .GetSnapshotAsync();
        var activeRuns = snapshot.ActivePermits.Count;

        var result = Compute(
            capacity,
            activeRuns,
            agent.MaxConcurrentRuns,
            _timeProvider.GetUtcNow(),
            runners.Count > 0,
            GateWaitingCount(snapshot));
        return result;
    }

    public async Task<IReadOnlyDictionary<string, AgentAvailabilityListEntry>> GetListSummaryAsync(
        string projectId,
        IReadOnlyCollection<AgentInfo> agents,
        CancellationToken ct = default)
    {
        // Runner capacity is fetched exactly once per request — the list
        // summary's core cost-control guarantee. Per-Agent active counts
        // are cheap in-process grain calls; pending-job counts come from
        // a single batched query grouped by Agent.
        var runners = await _runnerStatus.GetOnlineRunnersAsync(projectId, ct);
        var capacity = SumCapacity(runners);
        var hasOnlineRunner = runners.Count > 0;
        var pendingCounts = agents.Count == 0
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : await _jobs.CountPendingByAgentAsync(projectId, ct);

        var observedAt = _timeProvider.GetUtcNow();
        var entries = new Dictionary<string, AgentAvailabilityListEntry>(agents.Count, StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            var snapshot = await _grains
                .GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agent.Id))
                .GetSnapshotAsync();
            var pendingCount = pendingCounts.TryGetValue(agent.Id, out var count) ? count : 0;
            var followupWaiters = snapshot.Waiters.Count(waiter =>
                waiter.OwnerKind == AgentConcurrencyPermitOwnerKind.Followup);
            var pendingFollowupNotifications = snapshot.PendingNotifications.Count(notification =>
                notification.OwnerKind == AgentConcurrencyPermitOwnerKind.Followup);
            var queuedCount = pendingCount + followupWaiters + pendingFollowupNotifications;
            entries[agent.Id] = BuildListEntry(
                agent,
                capacity,
                snapshot.ActivePermits.Count,
                queuedCount,
                hasOnlineRunner,
                observedAt,
                GateWaitingCount(snapshot));
        }

        return entries;
    }

    public static AgentAvailabilityListEntry BuildListEntry(
        AgentInfo agent,
        RunnerCapacityView capacity,
        int activeRuns,
        int queuedCount,
        bool hasOnlineRunner,
        DateTimeOffset observedAt,
        int gateWaiterCount = 0)
    {
        var availability = Compute(
            capacity,
            activeRuns,
            agent.MaxConcurrentRuns,
            observedAt,
            hasOnlineRunner,
            gateWaiterCount);
        return new AgentAvailabilityListEntry(
            agent.Id,
            availability.CanStartNow,
            availability.WaitingReason,
            activeRuns,
            agent.MaxConcurrentRuns,
            capacity,
            queuedCount);
    }

    public async Task<IReadOnlyList<AgentWaitingWork>> GetWaitingWorkAsync(
        string projectId,
        AgentInfo agent,
        AgentAvailabilityResult availability,
        CancellationToken ct = default)
    {
        var snapshot = await _grains
            .GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agent.Id))
            .GetSnapshotAsync();
        var concurrencyJobs = snapshot.Waiters
            .Where(waiter => waiter.OwnerKind == AgentConcurrencyPermitOwnerKind.Job)
            .Select(waiter => waiter.OwnerId)
            .ToHashSet(StringComparer.Ordinal);

        var pending = await _jobs.ListByAgentAsync(
            projectId,
            agent.Id,
            [AgentJobStatus.Pending],
            ct: ct);

        var jobs = BuildWaitingWork(pending, concurrencyJobs, availability.WaitingReason);
        var followups = snapshot.Waiters
            .Where(waiter => waiter.OwnerKind == AgentConcurrencyPermitOwnerKind.Followup)
            .Select(waiter => new AgentWaitingWork(
                waiter.OwnerId,
                "waiting",
                waiter.WaitingReason,
                null))
            .ToList();
        var pendingFollowupNotifications = snapshot.PendingNotifications
            .Where(notification => notification.OwnerKind == AgentConcurrencyPermitOwnerKind.Followup)
            .Select(notification => new AgentWaitingWork(
                notification.OwnerId,
                "waiting",
                AgentAvailabilityWaitReasons.DispatchPending,
                null))
            .ToList();
        return jobs.Concat(followups).Concat(pendingFollowupNotifications).ToList();
    }

    public static AgentAvailabilityResult Compute(
        RunnerCapacityView capacity,
        int activeRuns,
        int? maxConcurrentRuns,
        DateTimeOffset observedAt) =>
        Compute(capacity, activeRuns, maxConcurrentRuns, observedAt, capacity.TotalSlots > 0);

    public static AgentAvailabilityResult Compute(
        RunnerCapacityView capacity,
        int activeRuns,
        int? maxConcurrentRuns,
        DateTimeOffset observedAt,
        bool hasOnlineRunner)
        => Compute(capacity, activeRuns, maxConcurrentRuns, observedAt, hasOnlineRunner, 0);

    public static AgentAvailabilityResult Compute(
        RunnerCapacityView capacity,
        int activeRuns,
        int? maxConcurrentRuns,
        DateTimeOffset observedAt,
        bool hasOnlineRunner,
        int gateWaiterCount)
    {
        string? reason = !hasOnlineRunner
            ? AgentAvailabilityWaitReasons.NoOnlineRunner
            : capacity.UsedSlots >= capacity.TotalSlots
                ? AgentAvailabilityWaitReasons.CapacityFull
                : gateWaiterCount > 0
                    ? AgentAvailabilityWaitReasons.CapacityFull
                : maxConcurrentRuns is not null && activeRuns >= maxConcurrentRuns.Value
                    ? AgentAvailabilityWaitReasons.ConcurrencyLimit
                    : null;

        return new AgentAvailabilityResult(
            reason is null,
            reason,
            activeRuns,
            maxConcurrentRuns,
            capacity,
            observedAt);
    }

    public static IReadOnlyList<AgentWaitingWork> BuildWaitingWork(
        IReadOnlyList<AgentJobListItem> pending,
        IReadOnlySet<string> concurrencyJobs,
        string? availabilityReason) =>
        pending
            .Select(job => new AgentWaitingWork(
                job.JobKey,
                "waiting",
                concurrencyJobs.Contains(job.JobKey)
                    ? AgentAvailabilityWaitReasons.CapacityFull
                    : availabilityReason ?? AgentAvailabilityWaitReasons.DispatchPending,
                job.SubmittedAt))
            .ToList();

    private static int GateWaitingCount(AgentConcurrencySnapshot snapshot) =>
        snapshot.Waiters.Count + snapshot.PendingNotifications.Count;

    private static RunnerCapacityView SumCapacity(IReadOnlyList<RunnerStatusView> runners)
    {
        var used = 0;
        var total = 0;
        foreach (var runner in runners)
        {
            if (runner.Capacity is not { } runnerCapacity)
                continue;
            used += runnerCapacity.UsedSlots;
            total += runnerCapacity.TotalSlots;
        }
        return new RunnerCapacityView(used, total);
    }
}
