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

public sealed class AgentAvailabilityService : IScopedService
{
    private readonly RunnerStatusService _runnerStatus;
    private readonly IGrainFactory _grains;
    private readonly AgentJobQuerier _jobs;
    private readonly TimeProvider _timeProvider;

    public AgentAvailabilityService(
        RunnerStatusService runnerStatus,
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
        var runners = await _runnerStatus.GetOnlineRunnersAsync(projectId);
        var capacity = SumCapacity(runners);
        var activeRuns = await _grains
            .GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agent.Id))
            .GetActiveCountAsync();

        var result = Compute(
            capacity,
            activeRuns,
            agent.MaxConcurrentRuns,
            _timeProvider.GetUtcNow(),
            runners.Count > 0);
        return result;
    }

    public async Task<IReadOnlyList<AgentWaitingWork>> GetWaitingWorkAsync(
        string projectId,
        AgentInfo agent,
        AgentAvailabilityResult availability,
        CancellationToken ct = default)
    {
        var waiters = await _grains
            .GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agent.Id))
            .GetWaitersAsync();
        var concurrencyJobs = waiters
            .Select(waiter => waiter.JobId)
            .ToHashSet(StringComparer.Ordinal);

        var pending = await _jobs.ListByAgentAsync(
            projectId,
            agent.Id,
            [AgentJobStatus.Pending],
            ct: ct);

        return BuildWaitingWork(pending, concurrencyJobs, availability.WaitingReason);
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
    {
        string? reason = !hasOnlineRunner
            ? AgentAvailabilityWaitReasons.NoOnlineRunner
            : capacity.UsedSlots >= capacity.TotalSlots
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
                    ? AgentAvailabilityWaitReasons.ConcurrencyLimit
                    : availabilityReason ?? AgentAvailabilityWaitReasons.NoOnlineRunner,
                job.SubmittedAt))
            .ToList();

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
