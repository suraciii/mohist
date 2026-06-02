using System.Collections.Concurrent;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Scheduling;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Tests.Support;

public sealed class InMemoryWorkflowScheduler : IWorkflowScheduler
{
    private readonly ConcurrentDictionary<string, WorkflowQueueRow> _rows = new(StringComparer.Ordinal);

    public Task EnqueueAsync(string workflowRunId, string projectId, CancellationToken cancellationToken = default)
    {
        _rows.AddOrUpdate(
            workflowRunId,
            _ => new WorkflowQueueRow { WorkflowRunId = workflowRunId, ProjectId = projectId },
            (_, row) =>
            {
                if (row.State == WorkflowQueueStates.Queued)
                    row.ProjectId = projectId;
                return row;
            });
        return Task.CompletedTask;
    }

    public Task RequeueAsync(string workflowRunId, string projectId, CancellationToken cancellationToken = default)
    {
        _rows[workflowRunId] = new WorkflowQueueRow { WorkflowRunId = workflowRunId, ProjectId = projectId };
        return Task.CompletedTask;
    }

    public Task ClearAsync(string workflowRunId, CancellationToken cancellationToken = default)
    {
        _rows.TryRemove(workflowRunId, out _);
        return Task.CompletedTask;
    }

    public Task ClearQueuedAsync(string workflowRunId, CancellationToken cancellationToken = default)
    {
        if (_rows.TryGetValue(workflowRunId, out var row) && row.State == WorkflowQueueStates.Queued)
            _rows.TryRemove(workflowRunId, out _);
        return Task.CompletedTask;
    }

    public Task<WorkflowQueueRow?> GetAsync(string workflowRunId, CancellationToken cancellationToken = default)
    {
        _rows.TryGetValue(workflowRunId, out var row);
        return Task.FromResult(row);
    }

    public Task<WorkflowQueueRow?> ClaimAsync(string runnerId, IReadOnlyList<string> projectIds, int maxActiveLeases, CancellationToken cancellationToken = default)
    {
        var active = _rows.Values.Count(row => row.State == WorkflowQueueStates.Leased && row.RunnerId == runnerId);
        if (active >= maxActiveLeases) return Task.FromResult<WorkflowQueueRow?>(null);

        var row = _rows.Values
            .Where(row => row.State == WorkflowQueueStates.Queued && (projectIds.Count == 0 || projectIds.Contains(row.ProjectId)))
            .OrderBy(row => row.UpdatedAt)
            .FirstOrDefault();
        if (row is null) return Task.FromResult<WorkflowQueueRow?>(null);

        row.State = WorkflowQueueStates.Leased;
        row.RunnerId = runnerId;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        return Task.FromResult<WorkflowQueueRow?>(row);
    }

    public Task AttachLeaseAsync(string workflowRunId, string projectId, string runnerId, WorkLease lease, CancellationToken cancellationToken = default)
    {
        _rows[workflowRunId] = new WorkflowQueueRow
        {
            WorkflowRunId = workflowRunId,
            ProjectId = projectId,
            State = WorkflowQueueStates.Leased,
            RunnerId = runnerId,
            WorkId = lease.WorkId,
            WorkType = lease.WorkType,
            Stage = lease.Stage,
            LogicalId = lease.LogicalId,
            Title = lease.Title,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task HeartbeatRunnerAsync(string runnerId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<int> ActiveLeaseCountAsync(string runnerId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_rows.Values.Count(row => row.State == WorkflowQueueStates.Leased && row.RunnerId == runnerId));
    }

    public Task<int> ExpireLeasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
}
