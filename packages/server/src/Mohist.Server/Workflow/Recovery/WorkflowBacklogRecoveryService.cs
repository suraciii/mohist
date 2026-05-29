using Microsoft.EntityFrameworkCore;
using Mohist.Server.Grains;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Workflow.Recovery;

public sealed class WorkflowBacklogRecoveryService : IHostedService
{
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
        var rows = await db.WorkflowRunStates
            .AsNoTracking()
            .Select(row => new { row.WorkflowRunId, row.StateJson })
            .ToListAsync(cancellationToken);

        var recovered = 0;

        foreach (var row in rows)
        {
            if (!TryRestoreRunnableWorkflow(row.StateJson, out var projectId, out var hasWork)) continue;
            if (!hasWork) continue;

            var backlog = _grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject(projectId ?? "default"));
            await backlog.RegisterAsync(row.WorkflowRunId);
            recovered++;
        }

        if (recovered > 0)
            _log.LogInformation("Recovered {Count} runnable workflows into backlog", recovered);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool TryRestoreRunnableWorkflow(string jsonState, out string? projectId, out bool hasWork)
    {
        projectId = null;
        hasWork = false;

        try
        {
            var state = WorkflowRunStore.Deserialize(jsonState);
            if (state is null) return false;

            projectId = state.ProjectId;

            var run = state.Run;
            if (!run.Started) return false;

            if (IsTerminal(run)) return true;

            var currentIndex = Math.Clamp(run.CurrentStageIndex, 0, run.Stages.Count - 1);
            if (run.Stages.Count == 0 || currentIndex >= run.Stages.Count) return false;

            var currentStage = run.Stages[currentIndex];
            hasWork = HasPendingWork(currentStage);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTerminal(WorkflowRunSnapshot run)
    {
        var currentIndex = Math.Clamp(run.CurrentStageIndex, 0, run.Stages.Count - 1);
        if (currentIndex >= run.Stages.Count || run.Stages.Count == 0) return true;
        var currentStage = run.Stages[currentIndex];

        if (currentStage.Failure is not null && !run.Paused) return true;

        if (currentStage.Initialized
            && currentStage.Tasks.All(t => t.Status == TaskRunStatus.Completed)
            && currentStage.Checks.All(c => c.Status == CheckRunStatus.Passed)
            && (!currentStage.RequiresApproval || currentStage.Approval?.Status == "approved")
            && currentStage.Order == run.Stages.Max(s => s.Order))
            return true;

        return false;
    }

    private static bool HasPendingWork(StageRunSnapshot stage)
    {
        if (!stage.Initialized) return true;

        return stage.Tasks.Any(t => t.Status is TaskRunStatus.Pending or TaskRunStatus.Running)
            || stage.Checks.Any(c => c.Status is CheckRunStatus.Pending);
    }
}
