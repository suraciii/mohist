using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Contracts;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Runner.Services.SignalR;

/// <summary>
/// Server-side router that delivers workflow terminal lifecycle events
/// (<c>WorkflowRunCompleted</c>, <c>WorkflowRunStopped</c>)
/// to the runner currently mapped to the workflow worker via the SignalR method
/// <c>ReceiveWorkflowRunStatus({ workflowRunId, status })</c>.
///
/// The server stays the source of truth for workflow lifecycle facts but
/// never schedules, scans, or performs runner filesystem deletion. The
/// router only consults workflow grain state to discover the assigned
/// worker; the runner owns the workspace and decides what to do with the
/// notification.
///
/// Routing rules:
/// <list type="bullet">
/// <item>If the mapped runner is offline (no <see cref="RunnerConnectionTracker"/> entry), the notification is dropped — the runner's convergence backstop (POST /workflow-runs/status) is authoritative.</item>
/// <item>If the workflow has no assigned worker, the notification is dropped.</item>
/// <item>Push failures are logged but do not fail the workflow event handler; lifecycle events must never be blocked on SignalR delivery.</item>
/// </list>
/// </summary>
public interface IRunnerWorkflowStatusRouter
{
    /// <summary>
    /// Push <c>ReceiveWorkflowRunStatus</c> to the runner currently
    /// assigned to <paramref name="workflowRunId"/>. No-op when the run
    /// has no assignment or the assigned worker has no connected runner.
    /// </summary>
    Task RouteAsync(string workflowRunId, WorkflowRunStatus status, CancellationToken ct = default);
}

public sealed class RunnerWorkflowStatusRouter : IRunnerWorkflowStatusRouter
{
    private readonly IHubContext<RunnerHub> _hub;
    private readonly RunnerConnectionTracker _connections;
    private readonly IGrainFactory _grains;
    private readonly ILogger<RunnerWorkflowStatusRouter> _log;

    public RunnerWorkflowStatusRouter(
        IHubContext<RunnerHub> hub,
        RunnerConnectionTracker connections,
        IGrainFactory grains,
        ILogger<RunnerWorkflowStatusRouter> log)
    {
        _hub = hub;
        _connections = connections;
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

        var connectionId = _connections.GetConnectionId(assignedWorkerId);
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            _log.LogDebug(
                "Terminal status router: worker {WorkerId} for {WorkflowRunId} has no connected runner, skipping push (convergence backstop will reconcile)",
                assignedWorkerId, workflowRunId);
            return;
        }

        var payload = new WorkflowRunStatusNotification(workflowRunId, status.ToString());
        try
        {
            await _hub.Clients
                .Client(connectionId)
                .SendCoreAsync("ReceiveWorkflowRunStatus", new object?[] { payload }, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Terminal status router: failed to push ReceiveWorkflowRunStatus to {WorkerId} for {WorkflowRunId}",
                assignedWorkerId, workflowRunId);
        }
    }
}
