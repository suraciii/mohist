using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;

namespace Mohist.Server.Runner.Services;

public class RunnerStatusService : IScopedService, IRunnerStatusSource
{
    private readonly IGrainFactory _grainFactory;
    private readonly RunnerConnectionTracker _connectionTracker;
    private readonly TimeProvider _timeProvider;
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(2);

    public RunnerStatusService(IGrainFactory grainFactory, RunnerConnectionTracker connectionTracker, TimeProvider timeProvider)
    {
        _grainFactory = grainFactory;
        _connectionTracker = connectionTracker;
        _timeProvider = timeProvider;
    }

    public virtual async Task<IReadOnlyList<RunnerStatusView>> GetRunnersAsync(string projectId)
    {
        var registry = _grainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);

        var eligible = await registry.ListEligibleRunnersAsync(projectId);

        var views = new List<RunnerStatusView>();
        foreach (var info in eligible)
        {
            var view = await ProjectRunnerAsync(info);
            views.Add(view);
        }

        return views;
    }

    public async Task<IReadOnlyList<RunnerStatusView>> GetOnlineRunnersAsync(string projectId)
        => await GetOnlineRunnersAsync(projectId, CancellationToken.None);

    public async Task<IReadOnlyList<RunnerStatusView>> GetOnlineRunnersAsync(string projectId, CancellationToken ct)
    {
        var registry = _grainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var eligible = await registry.ListEligibleRunnersAsync(projectId);

        var views = new List<RunnerStatusView>();
        foreach (var info in eligible)
        {
            if (!await IsRunnerOnlineAsync(info.RunnerId))
                continue;

            var view = await ProjectRunnerAsync(info);
            // ProjectRunnerAsync may have observed the grain becoming stale between
            // the filter call and the projection; drop it if the projection no
            // longer has a capacity (runtime was lost between reads).
            if (view.Capacity is null)
                continue;

            views.Add(view);
        }

        return views;
    }

    public async Task<RunnerCapacityView> GetCapacityAsync(string projectId)
    {
        var runners = await GetOnlineRunnersAsync(projectId);
        var used = 0;
        var total = 0;
        foreach (var runner in runners)
        {
            var capacity = runner.Capacity;
            if (capacity is null)
                continue;

            used += capacity.UsedSlots;
            total += capacity.TotalSlots;
        }
        return new RunnerCapacityView(used, total);
    }

    public async Task<RunnerStatusView?> GetRunnerAsync(string projectId, string runnerId)
    {
        if (string.IsNullOrWhiteSpace(runnerId))
        {
            return null;
        }

        var registry = _grainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var eligible = await registry.ListEligibleRunnersAsync(projectId);
        var info = eligible.FirstOrDefault(r => string.Equals(r.RunnerId, runnerId, StringComparison.Ordinal));
        if (info is null)
        {
            return null;
        }

        return await ProjectRunnerAsync(info);
    }

    private async Task<bool> IsRunnerOnlineAsync(string runnerId)
    {
        var runnerGrain = _grainFactory.GetGrain<IRunnerGrain>(runnerId);
        try
        {
            var runtime = await runnerGrain.GetRuntimeStateAsync();
            return runtime.Status == RunnerStatus.Online;
        }
        catch
        {
            return false;
        }
    }

    private async Task<RunnerStatusView> ProjectRunnerAsync(RunnerInfo info)
    {
        var runnerGrain = _grainFactory.GetGrain<IRunnerGrain>(info.RunnerId);
        RunnerRuntimeState? runtime = null;
        int? slots = null;
        try
        {
            runtime = await runnerGrain.GetRuntimeStateAsync();
            // Slots come from the runner grain's persisted definition state.
            slots = await runnerGrain.GetSlotsAsync();
        }
        catch
        {
            // Grain may be deactivated or unavailable
        }

        var now = _timeProvider.GetUtcNow();
        var connectionState = DeriveConnectionState(info.RunnerId);
        var status = DeriveStatus(info, runtime, connectionState, now);

        // Runners are global execution resources: the
        // RunnerInfo.ProjectId field is preserved on the wire for runner-line
        // compatibility but does not bind the runner to any project. The
        // scope view is therefore always "global".
        var scope = new RunnerScopeView("global");

        var activeWorkflowCount = runtime is not null
            ? runtime.ActiveWorks
                .Where(w => w.OwnerKind == WorkDispatchOwnerKinds.Workflow)
                .Select(w => w.OwnerId)
                .Distinct(StringComparer.Ordinal)
                .Count()
            : 0;

        var capacity = runtime is not null && slots.HasValue
            ? new RunnerCapacityView(activeWorkflowCount, slots.Value)
            : null;

        var activeWorks = ProjectActiveWorks(runtime?.ActiveWorks);

        return new RunnerStatusView(
            info.RunnerId,
            info.Kind,
            info.Hostname,
            scope,
            status,
            info.RegisteredAt,
            runtime?.LastHeartbeatAt,
            connectionState,
            info.Capabilities,
            info.CoderModels ?? [],
            info.CoderModels?.Length ?? 0,
            capacity,
            activeWorks,
            info.BuildGitHash);
    }

    private static IReadOnlyList<RunnerActiveWorkView> ProjectActiveWorks(IReadOnlyList<RunnerActiveWorkItem>? activeWorks)
    {
        if (activeWorks is null || activeWorks.Count == 0)
        {
            return [];
        }

        var views = new List<RunnerActiveWorkView>(activeWorks.Count);
        foreach (var work in activeWorks)
        {
            views.Add(new RunnerActiveWorkView(
                work.WorkId,
                work.OwnerKind,
                work.OwnerId,
                work.WorkType,
                work.Stage,
                work.Title,
                work.Issue is null
                    ? null
                    : new RunnerActiveWorkIssueView(work.Issue.ProjectId, work.Issue.IssueNumber)));
        }
        return views;
    }

    private string DeriveStatus(RunnerInfo info, RunnerRuntimeState? runtime, string connectionState, DateTimeOffset now)
    {
        if (runtime is null)
            return "offline";

        if (runtime.Status == RunnerStatus.Offline)
            return "offline";

        var elapsed = now - runtime.LastHeartbeatAt;
        if (elapsed > StaleThreshold)
            return "stale";

        if (runtime.ActiveWorks.Count > 0)
            return "busy";

        var requiresLiveConnection = info.Capabilities.Contains("workspace-query", StringComparer.OrdinalIgnoreCase);
        if (requiresLiveConnection && !string.Equals(connectionState, "connected", StringComparison.Ordinal))
            return "offline";

        return "idle";
    }

    private string DeriveConnectionState(string runnerId)
    {
        var connectionId = _connectionTracker.GetConnectionId(runnerId);
        return connectionId is not null ? "connected" : "disconnected";
    }
}
