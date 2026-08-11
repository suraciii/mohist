using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Data.Workflow;

public sealed record WorkflowRunTaskMapEntry(string TaskId, string WorkId);

public sealed record WorkflowRunWorkProjectionData(
    string WorkflowRunId,
    IReadOnlyList<WorkflowRunTaskMapEntry> TaskMap,
    string? ActiveWorkId,
    string? ActiveWorkerId);

public static class WorkflowRunWorkProjectionBuilder
{
    public static WorkflowRunWorkProjectionData Build(WorkflowRun run)
    {
        var taskMap = run.Stages
            .SelectMany(stage => stage.Tasks)
            .Select(task => new WorkflowRunTaskMapEntry(task.Id, EffectiveWorkId(task.WorkId, task.Id)))
            .Distinct()
            .ToList();

        var assignedWorkerId = run.Assignment?.WorkerId;
            var active = string.IsNullOrWhiteSpace(assignedWorkerId)
                ? null
                : run.CurrentActiveWorkFor(assignedWorkerId);
            if (run.HasUnresolvedAgentResult())
                active = null;
        var activeWorkId = active is null
            ? null
            : EffectiveWorkId(active.WorkId, active.TaskRunId);

        return new WorkflowRunWorkProjectionData(
            run.Id,
            taskMap,
            activeWorkId,
            active is null ? null : assignedWorkerId);
    }

    private static string EffectiveWorkId(string? workId, string? fallback) =>
        string.IsNullOrWhiteSpace(workId) ? fallback ?? string.Empty : workId;
}

public interface IWorkflowRunWorkProjection
{
    Task<string?> ResolveWorkIdAsync(string workflowRunId, string taskId, CancellationToken ct = default);
    Task<string?> ResolveTaskIdAsync(string workflowRunId, string workId, CancellationToken ct = default);
    Task<bool> IsActiveWorkAsync(string workflowRunId, string workId, string runnerId, CancellationToken ct = default);
    Task<bool> IsTerminalWorkAsync(string workflowRunId, string workId, string runnerId, CancellationToken ct = default);
    Task<string?> GetProjectIdAsync(string workflowRunId, CancellationToken ct = default);
}

public sealed class WorkflowRunWorkProjection : IWorkflowRunWorkProjection
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowRunWorkProjection(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<string?> ResolveWorkIdAsync(
        string workflowRunId,
        string taskId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId) || string.IsNullOrWhiteSpace(taskId))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowRunTaskMaps
            .AsNoTracking()
            .Where(row => row.WorkflowRunId == workflowRunId && row.TaskId == taskId)
            .Select(row => row.WorkId)
            .SingleOrDefaultAsync(ct);
    }

    public async Task<string?> ResolveTaskIdAsync(
        string workflowRunId,
        string workId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId) || string.IsNullOrWhiteSpace(workId))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowRunTaskMaps
            .AsNoTracking()
            .Where(row => row.WorkflowRunId == workflowRunId && row.WorkId == workId)
            .Select(row => row.TaskId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> IsActiveWorkAsync(
        string workflowRunId,
        string workId,
        string runnerId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId)
            || string.IsNullOrWhiteSpace(workId)
            || string.IsNullOrWhiteSpace(runnerId))
            return false;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var active = await db.WorkflowRuns
            .AsNoTracking()
            .Where(row => row.WorkflowRunId == workflowRunId)
            .Select(row => new { row.ActiveWorkId, row.ActiveWorkerId, row.Status })
            .SingleOrDefaultAsync(ct);

        return active is not null
            && !IsTerminalStatus(active.Status)
            && string.Equals(active.ActiveWorkId, workId, StringComparison.Ordinal)
            && string.Equals(active.ActiveWorkerId, runnerId, StringComparison.Ordinal);
    }

    private static bool IsTerminalStatus(string? status) =>
        status is not null
        && Enum.TryParse<WorkflowRunStatus>(status, ignoreCase: true, out var parsed)
        && parsed.IsTerminal();

    public async Task<bool> IsTerminalWorkAsync(
        string workflowRunId,
        string workId,
        string runnerId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId)
            || string.IsNullOrWhiteSpace(workId)
            || string.IsNullOrWhiteSpace(runnerId))
            return false;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var state = await db.WorkflowRuns
            .AsNoTracking()
            .Where(row => row.WorkflowRunId == workflowRunId)
            .Select(row => row.State)
            .SingleOrDefaultAsync(ct);
        if (state is null)
            return false;

        var run = JSON.Deserialize<WorkflowRun>(state);
        if (run is null)
            return false;

        var settledTask = FindSettledTask(run);
        if (settledTask is null)
            return false;

        var settledWorkId = string.IsNullOrWhiteSpace(settledTask.WorkId)
            ? settledTask.Id
            : settledTask.WorkId;
        return string.Equals(settledWorkId, workId, StringComparison.Ordinal)
            && string.Equals(settledTask.WorkerId, runnerId, StringComparison.Ordinal);
    }

    private static TaskRun? FindSettledTask(WorkflowRun run)
    {
        if (!run.Status.IsTerminal() || string.IsNullOrWhiteSpace(run.CurrentStageId))
            return null;

        var currentStage = run.Stages.FirstOrDefault(
            stage => string.Equals(stage.Id, run.CurrentStageId, StringComparison.Ordinal));
        if (currentStage is null)
            return null;

        if (run.Status == WorkflowRunStatus.Stopped)
        {
            var interruptedTasks = currentStage.Tasks.Where(task =>
                task.Status is TaskRunStatus.Running or TaskRunStatus.Cancelled
                && !string.IsNullOrWhiteSpace(task.WorkerId)
                && (!string.IsNullOrWhiteSpace(task.WorkId) || !string.IsNullOrWhiteSpace(task.Id)))
                .Take(2)
                .ToArray();
            return interruptedTasks.Length == 1 ? interruptedTasks[0] : null;
        }

        return currentStage.Tasks.LastOrDefault(task =>
            (task.Status is TaskRunStatus.Completed or TaskRunStatus.Failed or TaskRunStatus.Cancelled)
            && !string.IsNullOrWhiteSpace(task.WorkerId)
            && (!string.IsNullOrWhiteSpace(task.WorkId) || !string.IsNullOrWhiteSpace(task.Id)));
    }

    public async Task<string?> GetProjectIdAsync(string workflowRunId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowRuns
            .AsNoTracking()
            .Where(row => row.WorkflowRunId == workflowRunId)
            .Select(row => row.MetadataProjectId)
            .SingleOrDefaultAsync(ct);
    }
}
