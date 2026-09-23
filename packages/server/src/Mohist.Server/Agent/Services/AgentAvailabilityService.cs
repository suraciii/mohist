using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Capacity;
using Mohist.Server.Infrastructure.Hosting;
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
    int? ActiveRuns,
    int? MaxConcurrentRuns,
    RunnerCapacityView Capacity,
    DateTimeOffset ObservedAt,
    IReadOnlyList<AgentCapacityQueueEntry> Queued,
    bool CapacityIncomplete = false);

public sealed record AgentWaitingWork(
    string JobId,
    string Status,
    string WaitingReason,
    string? SubmittedAt);

public sealed record AgentAvailabilityListEntry(
    string AgentId,
    bool CanStartNow,
    string? WaitingReason,
    int? ActiveRuns,
    int? MaxConcurrentRuns,
    RunnerCapacityView Capacity,
    int? QueuedCount,
    bool CapacityIncomplete = false);

public sealed class AgentAvailabilityService : IScopedService
{
    private readonly IRunnerStatusSource _runnerStatus;
    private readonly IAgentCapacityStore _capacityStore;
    private readonly AgentJobQuerier _jobs;
    private readonly TimeProvider _timeProvider;

    public AgentAvailabilityService(
        IRunnerStatusSource runnerStatus,
        IAgentCapacityStore capacityStore,
        AgentJobQuerier jobs,
        TimeProvider timeProvider)
    {
        _runnerStatus = runnerStatus;
        _capacityStore = capacityStore;
        _jobs = jobs;
        _timeProvider = timeProvider;
    }

    public async Task<AgentAvailabilityResult?> GetAsync(
        string projectId,
        AgentInfo agent,
        CancellationToken ct = default)
    {
        // One canonical Runner read per request; the per-Agent Runtime
        // requirement is projected from the same snapshot so availability
        // never advertises a Runtime the capability gate would reject. The
        // occupancy conclusion comes from one derived owner read, whose
        // queued projection the waiting-work list reuses without a second
        // capacity read.
        var runnerSnapshot = await _runnerStatus.GetGlobalRunnersAsync(ct);
        var availability = RunnerStatusService.ProjectAvailability(runnerSnapshot, RequirementFor(agent));
        var snapshots = await _capacityStore.ReadAsync(projectId, [agent.Id], ct);
        snapshots.TryGetValue(agent.Id, out var snapshot);

        return Compute(
            availability.Capacity,
            snapshot?.Occupied,
            snapshot?.MaxConcurrentRuns,
            _timeProvider.GetUtcNow(),
            availability.HasOnlineRunner,
            availability.BlockingReason,
            availability.CapacityIncomplete || snapshot is null || !snapshot.IsComplete,
            snapshot?.Queued ?? []);
    }

    public async Task<IReadOnlyDictionary<string, AgentAvailabilityListEntry>> GetListSummaryAsync(
        string projectId,
        IReadOnlyCollection<AgentInfo> agents,
        CancellationToken ct = default)
    {
        // Runner status and owner occupancy are each fetched exactly once per
        // request — the list summary's core cost-control guarantee. One
        // batched derived read serves every Agent's live limit, active count
        // and queued projection; per-Agent availability is then derived from
        // the one snapshot with each Agent's requirement.
        var runnerSnapshot = await _runnerStatus.GetGlobalRunnersAsync(ct);
        var snapshots = await _capacityStore.ReadAsync(
            projectId,
            agents.Select(agent => agent.Id).ToArray(),
            ct);

        var observedAt = _timeProvider.GetUtcNow();
        var entries = new Dictionary<string, AgentAvailabilityListEntry>(agents.Count, StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            var availability = RunnerStatusService.ProjectAvailability(runnerSnapshot, RequirementFor(agent));
            snapshots.TryGetValue(agent.Id, out var snapshot);
            entries[agent.Id] = BuildListEntry(
                agent,
                availability.Capacity,
                snapshot?.Occupied,
                snapshot?.Queued.Count,
                availability.HasOnlineRunner,
                observedAt,
                snapshot?.MaxConcurrentRuns,
                availability.BlockingReason,
                availability.CapacityIncomplete || snapshot is null || !snapshot.IsComplete);
        }

        return entries;
    }

    public static AgentAvailabilityListEntry BuildListEntry(
        AgentInfo agent,
        RunnerCapacityView capacity,
        int? activeRuns,
        int? queuedCount,
        bool hasOnlineRunner,
        DateTimeOffset observedAt,
        int? maxConcurrentRuns,
        string? runnerBlockingReason = null,
        bool capacityIncomplete = false)
    {
        var availability = Compute(
            capacity,
            activeRuns,
            maxConcurrentRuns,
            observedAt,
            hasOnlineRunner,
            runnerBlockingReason,
            capacityIncomplete);
        return new AgentAvailabilityListEntry(
            agent.Id,
            availability.CanStartNow,
            availability.WaitingReason,
            availability.ActiveRuns,
            availability.MaxConcurrentRuns,
            capacity,
            availability.CapacityIncomplete ? null : queuedCount,
            availability.CapacityIncomplete);
    }

    public async Task<IReadOnlyList<AgentWaitingWork>> GetWaitingWorkAsync(
        string projectId,
        AgentInfo agent,
        AgentAvailabilityResult availability,
        CancellationToken ct = default)
    {
        // Waiting work is derived from the same owner snapshot the conclusion
        // read: the detail request never performs a second capacity read.
        // A Turn entry keeps the established contract of carrying its
        // Session owner id in JobId.
        var pending = await _jobs.ListByAgentAsync(
            projectId,
            agent.Id,
            [AgentJobStatus.Pending],
            ct: ct);

        var jobs = BuildWaitingWork(pending, availability.WaitingReason);
        var turns = availability.Queued
            .Where(entry => entry.Kind == AgentCapacityOwnerKind.Turn)
            .Select(entry => new AgentWaitingWork(
                entry.OwnerId,
                "waiting",
                availability.WaitingReason ?? AgentAvailabilityWaitReasons.DispatchPending,
                entry.AcceptedAt.ToString("o")))
            .ToList();
        return jobs.Concat(turns).ToList();
    }

    public static AgentAvailabilityResult Compute(
        RunnerCapacityView capacity,
        int? activeRuns,
        int? maxConcurrentRuns,
        DateTimeOffset observedAt,
        bool hasOnlineRunner,
        string? runnerBlockingReason = null,
        bool capacityIncomplete = false,
        IReadOnlyList<AgentCapacityQueueEntry>? queued = null)
    {
        // Owner evidence that cannot be counted is neither an unlimited Agent
        // nor a full one: the conclusion stays dispatch-pending with the
        // unknown count visible, and only a missing online Runner outranks
        // that. The Agent's own saturation is the tightest constraint, so it
        // is reported before a pool-wide Runner blocker.
        var incomplete = capacityIncomplete || activeRuns is null;
        string? reason = !hasOnlineRunner
            ? AgentAvailabilityWaitReasons.NoOnlineRunner
            : incomplete
                ? AgentAvailabilityWaitReasons.DispatchPending
                : maxConcurrentRuns is { } limit && activeRuns is { } occupied && occupied >= limit
                    ? AgentAvailabilityWaitReasons.ConcurrencyLimit
                    : runnerBlockingReason
                        ?? (capacity.UsedSlots >= capacity.TotalSlots
                            ? AgentAvailabilityWaitReasons.CapacityFull
                            : null);

        return new AgentAvailabilityResult(
            reason is null,
            reason,
            activeRuns,
            maxConcurrentRuns,
            capacity,
            observedAt,
            queued ?? [],
            incomplete);
    }

    private static RunnerRuntimeRequirement? RequirementFor(AgentInfo agent) =>
        agent.EffectiveExecutionConfig is { } execution
            ? new RunnerRuntimeRequirement(execution.Runtime, execution.Model, execution.Variant)
            : null;

    public static IReadOnlyList<AgentWaitingWork> BuildWaitingWork(
        IReadOnlyList<AgentJobListItem> pending,
        string? availabilityReason) =>
        pending
            .Select(job => new AgentWaitingWork(
                job.JobKey,
                "waiting",
                availabilityReason ?? AgentAvailabilityWaitReasons.DispatchPending,
                job.SubmittedAt))
            .ToList();
}
