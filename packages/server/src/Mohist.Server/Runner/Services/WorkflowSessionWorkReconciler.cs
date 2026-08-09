using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Runner.Services;

/// <summary>
/// Bridges a terminal Session stop fact to the exact WorkflowRun work that
/// opened the session. The session is not a child aggregate of the workflow;
/// the persisted labels are therefore only a binding lookup, while the
/// Workflow grain remains the owner of task state and active-work projection.
/// </summary>
public sealed class WorkflowSessionWorkReconciler(
    AgentSessionQuery sessions,
    IGrainFactory grains) : IScopedService
{
    public async Task ReconcileAsync(
        string projectId,
        string sessionId,
        string? expectedRunnerId,
        string reason,
        CancellationToken ct = default)
    {
        var record = (await sessions.ListByIdsAsync([sessionId], ct)).FirstOrDefault();
        if (record is null
            || !string.Equals(record.Label(AgentSessionQueryMetadataKeys.ProjectId), projectId, StringComparison.Ordinal)
            || !string.Equals(record.Label(AgentSessionQueryMetadataKeys.SourceKind), "workflow", StringComparison.Ordinal))
        {
            return;
        }

        var workflowRunId = record.Label(AgentSessionQueryMetadataKeys.WorkflowRunId);
        var workId = record.Label(AgentSessionQueryMetadataKeys.WorkId);
        var runnerId = record.Session.Runtime.RunnerId;
        if (string.IsNullOrWhiteSpace(workflowRunId)
            || string.IsNullOrWhiteSpace(workId)
            || string.IsNullOrWhiteSpace(runnerId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(expectedRunnerId)
            && !string.Equals(expectedRunnerId, runnerId, StringComparison.Ordinal))
        {
            return;
        }

        await grains.GetGrain<IWorkflowGrain>(workflowRunId)
            .AbandonActiveWorkAsync(runnerId, workId, reason);
    }
}
