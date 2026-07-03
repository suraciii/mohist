using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Runner.Services;

/// <summary>
/// Service facade that the API layer talks to for task-log persistence.
/// Wraps <see cref="TaskLogStore"/> so API code stays out of the
/// persistence layer (architectural rule: API → Services, not
/// API → Infrastructure.Data). TaskLog is review evidence associated
/// with a work item — independent of status adjudication by design D1,
/// so this service performs no grain calls.
/// </summary>
public sealed class TaskLogService : IScopedService
{
    private readonly TaskLogStore _store;
    private readonly WorkflowRunQuerier _runQuerier;

    public TaskLogService(TaskLogStore store, WorkflowRunQuerier runQuerier)
    {
        _store = store;
        _runQuerier = runQuerier;
    }

    /// <summary>
    /// Persists the runner's terminal-batch upload. <paramref name="truncated"/>
    /// reports whether head lines were dropped so the web client can
    /// surface that to users.
    /// </summary>
    public Task AppendAsync(
        string ownerKind,
        string ownerId,
        string workId,
        IReadOnlyList<TaskLogLine> entries,
        bool truncated,
        CancellationToken ct = default)
        => _store.AppendAsync(ownerKind, ownerId, workId, entries, truncated, ct);

    /// <summary>
    /// Cursor-paginated query over a work item's captured lines,
    /// resolved from a timeline task id (<c>TaskRun.Id</c>) to a
    /// <c>WorkId</c> via the persisted workflow-run state (no grain
    /// call). Returns <c>null</c> when the run, task, or work id
    /// cannot be located — the API surfaces that as an empty page
    /// (never an error, per the spec's no-log scenario).
    /// </summary>
    public async Task<TaskLogPage?> QueryByTaskIdAsync(
        string workflowRunId,
        string taskId,
        long? afterSeq,
        int? limit,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId) || string.IsNullOrWhiteSpace(taskId))
            return null;

        var workId = await ResolveWorkIdAsync(workflowRunId, taskId, ct);
        if (workId is null)
            return null;

        return await _store.QueryAsync(
            TaskLogOwnershipKinds.Workflow,
            workflowRunId,
            workId,
            afterSeq,
            limit,
            ct);
    }

    private async Task<string?> ResolveWorkIdAsync(string workflowRunId, string taskId, CancellationToken ct)
    {
        var run = await _runQuerier.LoadAsync(workflowRunId, ct);
        if (run is null) return null;

        foreach (var stage in run.Stages)
        {
            foreach (var task in stage.Tasks)
            {
                if (string.Equals(task.Id, taskId, StringComparison.Ordinal) && !string.IsNullOrEmpty(task.WorkId))
                    return task.WorkId;
            }
        }

        return null;
    }
}

/// <summary>
/// Owner-kind constants shared between the route layer and the
/// store. The runner computes these at flush time using the same
/// algorithm as <c>artifact-side-effects.ts:107</c>.
/// </summary>
public static class TaskLogOwnershipKinds
{
    public const string Workflow = "workflow";
    public const string AgentJob = "agent-job";
}