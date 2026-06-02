using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.SignalR;

namespace Mohist.Server.Runner.Projection;

public class RunnerStatusProjectionService
{
    private readonly IGrainFactory _grainFactory;
    private readonly RunnerConnectionTracker _connectionTracker;
    private readonly TimeProvider _timeProvider;
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(2);

    public RunnerStatusProjectionService(IGrainFactory grainFactory, RunnerConnectionTracker connectionTracker, TimeProvider timeProvider)
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
            ? new RunnerCapacityView(runtime.ActiveWork.Select(work => work.WorkflowRunId).Distinct(StringComparer.Ordinal).Count(), RunnerCapacity.Normalize(info.MaxWorkflowSlots))
            : null;

        var activeWork = runtime?.ActiveWork.FirstOrDefault();
        var activeWorkView = activeWork is not null
            ? new RunnerActiveWorkView(
                activeWork.WorkId,
                activeWork.WorkflowRunId,
                activeWork.WorkType,
                activeWork.Stage,
                activeWork.Title)
            : null;

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
            activeWorkView);
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

        if (runtime.ActiveWork.Count > 0)
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
