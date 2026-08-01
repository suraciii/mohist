using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Runner.Services;

/// <summary>
/// Service facade that the API layer talks to for task-log persistence.
/// Wraps <see cref="TaskLogStore"/> so API code stays out of the
/// persistence layer (architectural rule: API → Services, not
/// API → Infrastructure.Data). TaskLog is review evidence associated
/// with a work item — independent of status adjudication,
/// so this service performs no grain calls.
///
/// <para>
/// <b>Persistence vs. fan-out</b>. Each batch is persisted to the
/// authoritative store <i>before</i> any real-time fan-out is
/// attempted. The fan-out is best-effort: a publish throw (no
/// subscribers, per-connection send error, network drop) is logged
/// and swallowed, never blocking persistence or task execution.
/// This is the concrete form of the
/// "落库权威 + 实时分发 best-effort" invariant.
/// </para>
/// </summary>
public sealed class TaskLogService : IScopedService
{
    private readonly TaskLogStore _store;
    private readonly IAgentJobStore _agentJobs;
    private readonly IWorkflowRunWorkProjection _workProjection;
    private readonly ITaskLogDeltaPublisher _publisher;
    private readonly ILogger<TaskLogService> _log;

    public TaskLogService(
        TaskLogStore store,
        IAgentJobStore agentJobs,
        IWorkflowRunWorkProjection workProjection,
        ITaskLogDeltaPublisher publisher,
        ILogger<TaskLogService> log)
    {
        _store = store;
        _agentJobs = agentJobs;
        _workProjection = workProjection;
        _publisher = publisher;
        _log = log;
    }

    /// <summary>
    /// Persists a runner upload (incremental or terminal) and, on
    /// successful persistence, best-effort fans out the delta to
    /// subscribed SignalR connections via
    /// <see cref="ITaskLogDeltaPublisher"/>. Persistence is the
    /// authoritative step; fan-out failure (publisher throws, no
    /// subscribers, per-send error) is logged and swallowed so it
    /// never blocks the upload or the task's execution.
    /// <paramref name="truncated"/> reports whether head lines were
    /// dropped so the web client can surface that to users.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the work item is recognised as
    /// <c>Outstanding</c> and the batch is persisted; <c>false</c>
    /// when the work item is unknown or no longer outstanding (the
    /// caller surfaces that as a 4xx without touching real-time
    /// fan-out).
    /// </returns>
    public async Task<bool> AppendAsync(
        string runnerId,
        string ownerKind,
        string ownerId,
        string workId,
        IReadOnlyList<TaskLogLine> entries,
        bool truncated,
        bool terminal = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runnerId))
            throw new ArgumentException("runnerId must be provided", nameof(runnerId));

        ValidateBatchCaps(entries);

        if (!await IsActiveWorkAsync(runnerId, ownerKind, ownerId, workId, ct))
            return false;

        // 1. Persist FIRST. Authoritative; throw propagates (the
        //    upload route handles the validation/storing errors).
        await _store.AppendAsync(ownerKind, ownerId, workId, entries, truncated, terminal, ct);

        // 2. Best-effort fan-out, AFTER persistence has succeeded.
        //    A publisher throw, no-subscribers state, or per-send
        //    failure is logged and swallowed. This is the
        //    "persistence-before-distribution" invariant made
        //    concrete; the authoritative log is already on disk so
        //    any dropped delta is recoverable by the terminal
        //    reconciliation batch.
        try
        {
            var scope = await ResolvePublishScopeAsync(ownerKind, ownerId, workId, ct);
            var envelope = new TaskLogDeltaEnvelope(
                OwnerKind: ownerKind,
                OwnerId: ownerId,
                ProjectId: scope?.ProjectId,
                WorkId: workId,
                TaskId: scope?.TaskId,
                Entries: entries
                    .Select(e => new TaskLogDeltaEntry(e.Seq, e.Timestamp, e.Source, e.Text))
                    .ToList(),
                Truncated: truncated);

            await _publisher.PublishAsync(envelope, ct);
        }
        catch (Exception ex)
        {
            // Never let distribution failure break persistence or
            // the calling upload route. The authoritative log has
            // already been committed; dropping the realtime push
            // is the correct best-effort behaviour.
            _log.LogWarning(ex,
                "Task-log realtime fan-out failed for {OwnerKind}/{OwnerId}/{WorkId}; persistence unaffected",
                ownerKind, ownerId, workId);
        }

        return true;
    }

    /// <summary>
    /// Cursor-paginated query over a work item's captured lines,
    /// resolved from a timeline task id (<c>TaskRun.Id</c>) to a
    /// <c>WorkId</c> via the persisted workflow-run work projection
    /// (no grain call). Returns <c>null</c> when the run, task, or work id
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
        return await _workProjection.ResolveWorkIdAsync(workflowRunId, taskId, ct);
    }

    private async Task<bool> IsActiveWorkAsync(
        string runnerId,
        string ownerKind,
        string ownerId,
        string workId,
        CancellationToken ct)
    {
        if (string.Equals(ownerKind, TaskLogOwnershipKinds.Workflow, StringComparison.Ordinal))
            return await _workProjection.IsActiveWorkAsync(ownerId, workId, runnerId, ct);

        return (await _agentJobs.ListRunningForRunnerAsync(runnerId, ct))
            .Any(work => string.Equals(work.JobKey, ownerId, StringComparison.Ordinal)
                && string.Equals(work.WorkId, workId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Resolve <paramref name="workId"/> → <c>taskId</c> for the
    /// publish-time envelope stamp. Returns <c>null</c> when the
    /// work item isn't owned by a workflow run (e.g. an agent-job
    /// owner kind with no task mapping) or the work projection has no
    /// mapping; the publisher treats a null
    /// <c>taskId</c> as "no scope can match, no fan-out" (its
    /// <see cref="ConnectionSubscriptionRegistry.ShouldNotifyTaskLog"/>
    /// gate short-circuits to false).
    ///
    /// </summary>
    private async Task<TaskLogPublishScope?> ResolvePublishScopeAsync(
        string ownerKind,
        string ownerId,
        string workId,
        CancellationToken ct)
    {
        if (!string.Equals(ownerKind, TaskLogOwnershipKinds.Workflow, StringComparison.Ordinal))
            return null;
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(workId))
            return null;

        var taskId = await _workProjection.ResolveTaskIdAsync(ownerId, workId, ct);
        if (taskId is null)
            return null;

        var projectId = await _workProjection.GetProjectIdAsync(ownerId, ct);
        return new TaskLogPublishScope(taskId, projectId);
    }

    private static void ValidateBatchCaps(IReadOnlyList<TaskLogLine> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > TaskLogUploadLimits.MaxEntries)
            throw new ArgumentException($"Too many entries ({entries.Count}); max {TaskLogUploadLimits.MaxEntries}", nameof(entries));

        var totalTextLength = 0;
        foreach (var entry in entries)
        {
            totalTextLength += entry.Text?.Length ?? 0;
            if (totalTextLength > TaskLogUploadLimits.MaxTotalTextLength)
                throw new ArgumentException($"Task-log text payload exceeds {TaskLogUploadLimits.MaxTotalTextLength} characters", nameof(entries));
        }
    }
}

internal sealed record TaskLogPublishScope(string TaskId, string? ProjectId);

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
