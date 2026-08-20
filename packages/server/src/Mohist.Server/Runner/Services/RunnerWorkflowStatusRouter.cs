using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Contracts;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Runner.Services;

/// <summary>
/// Server-side router that delivers workflow terminal lifecycle events
/// (<c>WorkflowRunCompleted</c>, <c>WorkflowRunStopped</c>)
/// to the runner currently mapped to the workflow worker via the
/// <c>workflow.status-changed</c> control notification.
///
/// The server stays the source of truth for workflow lifecycle facts but
/// never schedules, scans, or performs runner filesystem deletion. The
/// router only consults workflow grain state to discover the assigned
/// worker; the runner owns the workspace and decides what to do with the
/// notification.
///
/// Routing rules:
/// <list type="bullet">
/// <item>If the mapped runner is offline, the notification is dropped — the runner's convergence backstop (POST /workflow-runs/status) is authoritative.</item>
/// <item>If the workflow has no assigned worker, the notification is dropped.</item>
/// <item>Push failures are logged but do not fail the workflow event handler; lifecycle events must never be blocked on control delivery.</item>
/// </list>
/// </summary>
public interface IRunnerWorkflowStatusRouter
{
    /// <summary>
    /// Push <c>workflow.status-changed</c> to the runner currently
    /// assigned to <paramref name="workflowRunId"/>. No-op when the run
    /// has no assignment or the assigned worker has no connected runner.
    /// </summary>
    Task RouteAsync(string workflowRunId, WorkflowRunStatus status, CancellationToken ct = default);
}

public sealed class RunnerWorkflowStatusRouter : IRunnerWorkflowStatusRouter
{
    private readonly IRunnerControlTransport _control;
    private readonly IGrainFactory _grains;
    private readonly ILogger<RunnerWorkflowStatusRouter> _log;

    public RunnerWorkflowStatusRouter(
        IRunnerControlTransport control,
        IGrainFactory grains,
        ILogger<RunnerWorkflowStatusRouter> log)
    {
        _control = control;
        _grains = grains;
        _log = log;
    }

    public async Task RouteAsync(string workflowRunId, WorkflowRunStatus status, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId) || !status.IsTerminal())
            return;

        IWorkflowGrain workflow;
        try
        {
            workflow = _grains.GetGrain<IWorkflowGrain>(workflowRunId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Terminal status router: failed to resolve workflow grain for {WorkflowRunId}",
                workflowRunId);
            return;
        }

        string? assignedWorkerId;
        try
        {
            assignedWorkerId = await workflow.GetAssignedWorkerIdAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Terminal status router: failed to read assignment for {WorkflowRunId}",
                workflowRunId);
            return;
        }

        if (string.IsNullOrWhiteSpace(assignedWorkerId))
        {
            _log.LogDebug(
                "Terminal status router: no assigned worker for {WorkflowRunId}, skipping push",
                workflowRunId);
            return;
        }

        var payload = new WorkflowRunStatusNotification(workflowRunId, status.ToString());
        try
        {
            await _control.SendNotificationAsync(
                assignedWorkerId,
                "workflow.status-changed",
                payload,
                ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Terminal status router: failed to push workflow.status-changed to {WorkerId} for {WorkflowRunId}",
                assignedWorkerId, workflowRunId);
        }
    }
}
