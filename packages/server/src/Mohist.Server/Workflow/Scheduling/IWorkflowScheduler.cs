using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Workflow.Scheduling;

public interface IWorkflowScheduler
{
    Task EnqueueAsync(string workflowRunId, string projectId, CancellationToken cancellationToken = default);
    Task RequeueAsync(string workflowRunId, string projectId, CancellationToken cancellationToken = default);
    Task ClearAsync(string workflowRunId, CancellationToken cancellationToken = default);
    Task ClearQueuedAsync(string workflowRunId, CancellationToken cancellationToken = default);
    Task<WorkflowQueueRow?> GetAsync(string workflowRunId, CancellationToken cancellationToken = default);
    Task<WorkflowQueueRow?> ClaimAsync(string runnerId, IReadOnlyList<string> projectIds, int maxActiveLeases, CancellationToken cancellationToken = default);
    Task AttachLeaseAsync(string workflowRunId, string projectId, string runnerId, WorkLease lease, CancellationToken cancellationToken = default);
    Task HeartbeatRunnerAsync(string runnerId, CancellationToken cancellationToken = default);
    Task<int> ActiveLeaseCountAsync(string runnerId, CancellationToken cancellationToken = default);
    Task<int> ExpireLeasesAsync(CancellationToken cancellationToken = default);
}
