using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;

namespace Mohist.Server.Runner.Services;

public class RunnerStatusService
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

    public async Task<IReadOnlyList<RunnerStatusView>> GetRunnersAsync(string projectId)
    {
        var registry = _grainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.ForProject(projectId));

        var eligible = await registry.ListEligibleRunnersAsync(projectId);

        var views = new List<RunnerStatusView>();
        foreach (var info in eligible)
        {
            var view = await ProjectRunnerAsync(info);
            views.Add(view);
        }

        return views;
    }

    public async Task<RunnerStatusView?> GetRunnerAsync(string projectId, string runnerId)
    {
        if (string.IsNullOrWhiteSpace(runnerId))
        {
            return null;
        }

        var registry = _grainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.ForProject(projectId));
        var eligible = await registry.ListEligibleRunnersAsync(projectId);
        var info = eligible.FirstOrDefault(r => string.Equals(r.RunnerId, runnerId, StringComparison.Ordinal));
        if (info is null)
        {
            return null;
        }

        return await ProjectRunnerAsync(info);
    }

    private async Task<RunnerStatusView> ProjectRunnerAsync(RunnerInfo info)
    {
        var runnerGrain = _grainFactory.GetGrain<IRunnerGrain>(info.RunnerId);
        RunnerRuntimeState? runtime = null;
        try
        {
            runtime = await runnerGrain.GetRuntimeStateAsync();
        }
        catch
        {
            // Grain may be deactivated or unavailable
        }

        var now = _timeProvider.GetUtcNow();
        var connectionState = DeriveConnectionState(info.RunnerId);
        var status = DeriveStatus(info, runtime, connectionState, now);

        var scope = string.IsNullOrWhiteSpace(info.ProjectId)
            ? new RunnerScopeView("global")
            : new RunnerScopeView("project", info.ProjectId, null);

        var capacity = runtime is not null
            ? new RunnerCapacityView(runtime.ActiveWorks.Count, RunnerCapacity.Normalize(info.MaxWorkflowSlots))
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
                    : new RunnerActiveWorkIssueView(work.Issue.ProjectId, work.Issue.IssueId, work.Issue.IssueNumber)));
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
