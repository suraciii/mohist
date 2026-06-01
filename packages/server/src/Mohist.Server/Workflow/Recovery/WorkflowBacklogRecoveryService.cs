using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Infrastructure.Persistence.Workflow;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Workflow.Recovery;

public sealed class WorkflowBacklogRecoveryService : IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IGrainFactory _grains;
    private readonly ILogger<WorkflowBacklogRecoveryService> _log;

    public WorkflowBacklogRecoveryService(
        IDbContextFactory<MohistDbContext> dbFactory,
        IGrainFactory grains,
        ILogger<WorkflowBacklogRecoveryService> log)
    {
        _dbFactory = dbFactory;
        _grains = grains;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.WorkflowRuns
            .AsNoTracking()
            .Select(row => new { row.WorkflowRunId, row.State, row.MetadataProjectId })
            .ToListAsync(cancellationToken);
        var variables = await db.WorkflowVariables
            .AsNoTracking()
            .ToDictionaryAsync(row => row.WorkflowRunId, row => row.StateJson, cancellationToken);
        var leases = await db.WorkflowLeases
            .AsNoTracking()
            .Select(row => row.WorkflowRunId)
            .ToHashSetAsync(cancellationToken);

        var backlogStates = await db.BacklogStates
            .AsNoTracking()
            .Select(row => new { row.ProjectId, row.StateJson })
            .ToListAsync(cancellationToken);
        var backlogSnapshots = backlogStates
            .Select(row => new BacklogSnapshot(row.ProjectId, row.StateJson))
            .ToList();

        var workflowStates = rows.ToDictionary(
            row => row.WorkflowRunId,
            row => BuildRecoveryState(row.WorkflowRunId, row.State, row.MetadataProjectId, variables.GetValueOrDefault(row.WorkflowRunId), leases.Contains(row.WorkflowRunId)),
            StringComparer.Ordinal);
        var staleWorkflowIds = new HashSet<string>(StringComparer.Ordinal);
        var recovered = 0;

        var runningClaims = new HashSet<string>(StringComparer.Ordinal);

        foreach (var backlogState in backlogSnapshots)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var workflowId in backlogState.StateJson is null
                ? []
                : DeserializeBacklog(backlogState.StateJson)?.Waiting ?? [])
            {
                if (!seen.Add(workflowId)) continue;
                ReconcileBacklogWorkflow(workflowId, workflowStates, staleWorkflowIds);
            }

            var state = backlogState.StateJson is null ? null : DeserializeBacklog(backlogState.StateJson);
            if (state is null) continue;

            foreach (var workflowId in state.Running.Keys)
            {
                runningClaims.Add(workflowId);
                if (!seen.Add(workflowId)) continue;
                ReconcileBacklogWorkflow(workflowId, workflowStates, staleWorkflowIds);
            }
        }

        foreach (var workflowId in leases)
            ReconcileBacklogWorkflow(workflowId, workflowStates, staleWorkflowIds);

        foreach (var workflowId in staleWorkflowIds)
        {
            if (!workflowStates.TryGetValue(workflowId, out var state))
            {
                await RemoveMissingWorkflowStateAsync(workflowId, backlogSnapshots, cancellationToken);
                continue;
            }

            var workflow = _grains.GetGrain<IWorkflowGrain>(workflowId);
            await workflow.UnscheduleAsync($"Startup recovery removed stale workflow scheduling state ({state.Status ?? "missing"})");
        }

        foreach (var state in workflowStates.Values)
        {
            if (!state.IsRunnable) continue;
            if (staleWorkflowIds.Contains(state.WorkflowRunId)) continue;
            if (string.IsNullOrWhiteSpace(state.ProjectId))
            {
                _log.LogWarning("Blocked backlog recovery for workflow {WorkflowRunId}: missing durable project identity", state.WorkflowRunId);
                continue;
            }

            var backlog = _grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject(state.ProjectId));
            if (state.HasLease)
            {
                if (!runningClaims.Contains(state.WorkflowRunId))
                    continue;

                var recoveredLease = await LoadLeaseAsync(state.WorkflowRunId, cancellationToken);
                if (recoveredLease is null || string.IsNullOrWhiteSpace(recoveredLease.RunnerId))
                {
                    var workflow = _grains.GetGrain<IWorkflowGrain>(state.WorkflowRunId);
                    await workflow.UnscheduleAsync("Startup recovery removed stale workflow lease without runner ownership");
                    continue;
                }

                if (!state.MatchesLeaseBackedDispatchableWork(recoveredLease))
                {
                    var workflowGrain = _grains.GetGrain<IWorkflowGrain>(state.WorkflowRunId);
                    await workflowGrain.UnscheduleAsync("Startup recovery removed stale running workflow claim after persisted state showed no dispatchable work");
                    continue;
                }

                await backlog.RestoreRunningAsync(state.WorkflowRunId, recoveredLease.RunnerId);
                var runner = _grains.GetGrain<Mohist.Server.Runner.Grains.IRunnerGrain>(recoveredLease.RunnerId);
                await runner.RestoreLeasedWorkAsync(
                    state.WorkflowRunId,
                    recoveredLease.WorkId,
                    recoveredLease.WorkType,
                    recoveredLease.Stage,
                    recoveredLease.Title);
                recovered++;
                continue;
            }

            await backlog.RegisterAsync(state.WorkflowRunId);
            recovered++;
        }

        if (recovered > 0)
            _log.LogInformation("Recovered {Count} runnable workflows into backlog", recovered);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<WorkLease?> LoadLeaseAsync(string workflowId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.WorkflowLeases.FindAsync([workflowId], cancellationToken);
        return row is null ? null : JsonSerializer.Deserialize<WorkLease>(row.StateJson, JsonOptions);
    }

    private async Task RemoveMissingWorkflowStateAsync(string workflowId, IReadOnlyList<BacklogSnapshot> backlogStates, CancellationToken cancellationToken)
    {
        foreach (var backlogState in backlogStates)
        {
            var state = DeserializeBacklog(backlogState.StateJson);
            if (state is null) continue;

            if (state.Waiting.Contains(workflowId) || state.Running.ContainsKey(workflowId) || state.All.Contains(workflowId))
            {
                var backlog = _grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject(backlogState.ProjectId));
                await backlog.ReleaseAsync(workflowId);
            }
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var lease = await db.WorkflowLeases.FindAsync([workflowId], cancellationToken);
        if (lease is not null)
            db.WorkflowLeases.Remove(lease);

        foreach (var backlogState in backlogStates)
        {
            var state = backlogState.StateJson is null ? null : DeserializeBacklog(backlogState.StateJson);
            if (state is null) continue;

            var waiting = state.Waiting.Where(id => !string.Equals(id, workflowId, StringComparison.Ordinal)).ToList();
            var running = state.Running
                .Where(kv => !string.Equals(kv.Key, workflowId, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var all = state.All.Where(id => !string.Equals(id, workflowId, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);

            if (waiting.Count == state.Waiting.Count && running.Count == state.Running.Count && all.Count == state.All.Count)
                continue;

            var row = await db.BacklogStates.FindAsync([backlogState.ProjectId], cancellationToken);
            if (row is null) continue;

            if (waiting.Count == 0 && running.Count == 0 && all.Count == 0)
            {
                db.BacklogStates.Remove(row);
                continue;
            }

            row.StateJson = JsonSerializer.Serialize(new WorkflowBacklogState(waiting, running, all), WorkflowStorageJson.Options);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private void ReconcileBacklogWorkflow(
        string workflowId,
        IReadOnlyDictionary<string, RecoveryWorkflowState> workflowStates,
        ISet<string> staleWorkflowIds)
    {
        if (!workflowStates.TryGetValue(workflowId, out var state))
        {
            _log.LogWarning("Startup recovery removing stale scheduling state for missing workflow {WorkflowRunId}", workflowId);
            staleWorkflowIds.Add(workflowId);
            return;
        }

        if (!state.IsRunnable)
        {
            _log.LogWarning("Startup recovery removing non-runnable workflow scheduling state for workflow {WorkflowRunId} (status={Status})", workflowId, state.Status ?? "missing");
            staleWorkflowIds.Add(workflowId);
            return;
        }
    }

    private static RecoveryWorkflowState BuildRecoveryState(string workflowRunId, string jsonState, string? indexedProjectId, string? variablesJson, bool hasLease)
    {
        if (!TryRestoreRunnableWorkflow(jsonState, out var run, out var hasWork))
            return new RecoveryWorkflowState(workflowRunId, indexedProjectId, null, false, hasLease, null);

        if (run is null)
            return new RecoveryWorkflowState(workflowRunId, indexedProjectId, null, false, hasLease, null);

        var projectId = ResolveProjectId(indexedProjectId, run, variablesJson);
        var isRunnable = hasWork && run.Status != WorkflowRunStatus.Paused && !IsTerminal(run);
        return new RecoveryWorkflowState(workflowRunId, projectId, run.Status.ToString(), isRunnable, hasLease, run);
    }

    private static WorkflowBacklogState? DeserializeBacklog(string json)
    {
        try { return JsonSerializer.Deserialize<WorkflowBacklogState>(json, WorkflowStorageJson.Options); }
        catch { return null; }
    }

    private static bool TryRestoreRunnableWorkflow(string jsonState, out WorkflowRun? run, out bool hasWork)
    {
        run = null;
        hasWork = false;

        try
        {
            run = Deserialize(jsonState);
            if (run is null) return false;

            if (run.StartedAt is null) return false;

            if (run.Status == WorkflowRunStatus.Paused) return false;

            if (IsTerminal(run)) return true;

            if (run.CurrentStageId is null) return false;
            var currentStageId = run.CurrentStageId;
            var currentStage = run.Stages.FirstOrDefault(s => s.Id == currentStageId);
            if (currentStage is null) return false;

            hasWork = HasPendingWork(currentStage);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTerminal(WorkflowRun run)
    {
        if (run.CurrentStageId is null) return true;
        var currentStage = run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId);
        if (currentStage is null) return true;

        if (currentStage.Failure is not null && run.Status != WorkflowRunStatus.Paused) return true;

        if (currentStage.Initialized
            && currentStage.Tasks.All(t => t.Status == TaskRunStatus.Completed)
            && currentStage.Checks.All(c => c.Status == StageCheckStatus.Passed)
            && (!currentStage.RequiresApproval || currentStage.ApprovalStatus is { Result: "approved" })
            && currentStage == run.Stages[^1])
            return true;

        return false;
    }

    private static bool HasPendingWork(StageRun stage)
    {
        if (!stage.Initialized) return true;

        return stage.Tasks.Any(t => t.Status is TaskRunStatus.Pending or TaskRunStatus.Running)
            || stage.Checks.Any(c => c.Status is StageCheckStatus.Pending);
    }

    private static LeaseBackedDispatchableWork? GetLeaseBackedDispatchableWork(WorkflowRun run)
    {
        if (run.CurrentStageId is null) return null;

        var currentStage = run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId);
        if (currentStage is null) return null;
        if (!currentStage.Initialized) return new LeaseBackedDispatchableWork(null, currentStage.Id, "stage-init", currentStage.Id);

        var currentTask = currentStage.Tasks.FirstOrDefault(t => t.Status is TaskRunStatus.Pending or TaskRunStatus.Running);
        if (currentTask is not null)
        {
            return new LeaseBackedDispatchableWork(currentTask.Id, currentStage.Id, "task", currentTask.DefinitionId);
        }

        var currentCheck = currentStage.Checks.FirstOrDefault(c => c.Status is StageCheckStatus.Pending);
        return currentCheck is not null
            ? new LeaseBackedDispatchableWork($"checks-{currentStage.Id}", currentStage.Id, "checks", $"checks-{currentStage.Id}")
            : null;
    }

    private static WorkflowRun? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<WorkflowRun>(json, JsonOptions); }
        catch { return null; }
    }

    private static string? ResolveProjectId(string? indexedProjectId, WorkflowRun run, string? variablesJson)
    {
        if (!string.IsNullOrWhiteSpace(indexedProjectId)) return indexedProjectId;

        if (run.Metadata.Annotations is not null
            && run.Metadata.Annotations.TryGetValue("projectId", out var annotationProjectId)
            && !string.IsNullOrWhiteSpace(annotationProjectId))
            return annotationProjectId;

        var variables = DeserializeVariables(variablesJson);
        return variables?.String("project", "id");
    }

    private static WorkflowExecutionContext? DeserializeVariables(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try { return JsonSerializer.Deserialize<WorkflowExecutionContext>(json, JsonOptions); }
        catch { return null; }
    }

    private sealed record BacklogSnapshot(string ProjectId, string StateJson);

    private sealed record RecoveryWorkflowState(
        string WorkflowRunId,
        string? ProjectId,
        string? Status,
        bool IsRunnable,
        bool HasLease,
        WorkflowRun? Run)
    {
        public bool MatchesLeaseBackedDispatchableWork(WorkLease lease)
        {
            if (!HasLease) return false;
            if (Run is null) return false;

            var dispatchable = GetLeaseBackedDispatchableWork(Run);
            if (dispatchable is null) return false;

            if (!string.Equals(dispatchable.Stage, lease.Stage, StringComparison.Ordinal)) return false;
            if (!string.Equals(dispatchable.WorkType, lease.WorkType, StringComparison.Ordinal)) return false;
            if (string.Equals(dispatchable.LogicalId, lease.LogicalId, StringComparison.Ordinal)) return true;

            return lease.WorkType == "task"
                && string.Equals(dispatchable.WorkId, lease.WorkId, StringComparison.Ordinal);
        }
    }

    private sealed record LeaseBackedDispatchableWork(
        string? WorkId,
        string Stage,
        string WorkType,
        string LogicalId);
}
