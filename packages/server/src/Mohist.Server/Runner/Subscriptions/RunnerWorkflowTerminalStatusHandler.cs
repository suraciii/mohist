using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Runner.Subscriptions;

/// <summary>
/// Server-side push handler for workflow terminal lifecycle events. When a
/// workflow run reaches <see cref="WorkflowRunStatus.Completed"/> or
/// <see cref="WorkflowRunStatus.Stopped"/>, this handler asks the runner
/// that owns the workspace to flip its local registry entry to
/// cleanup-eligible.
///
/// <c>Failed</c> is intentionally excluded: it is a recoverable mid-state
/// (Retry/Rerun/RerunFromStage revive it), so a failed run's workspace
/// must be preserved for the next dispatch. Treating <c>Failed</c> as
/// terminal caused retrying runs to have their workspaces reclaimed
/// mid-retry, losing plan/build artifacts.
///
/// The server never performs filesystem deletion; it only delivers the
/// notification. The runner's convergence backstop
/// (POST /api/runner/{runnerId}/workflow-runs/status) catches missed
/// events when the SignalR push cannot be delivered (runner offline at
/// terminal moment, transient SignalR failure, etc.).
///
/// Push subscriptions for the two terminal event types are registered as a
/// pipe-separated pattern so a single handler instance serves both types.
/// </summary>
[EventPush(
    Type = "com.mohist.workflow.run.completed|com.mohist.workflow.run.stopped",
    Identity = "Mohist.Server.Events.Subscriptions.RunnerWorkflowTerminalStatusHandler")]
public sealed class RunnerWorkflowTerminalStatusHandler : ICloudEventPushHandler
{
    private readonly IRunnerWorkflowStatusRouter _router;
    private readonly ILogger<RunnerWorkflowTerminalStatusHandler> _log;

    public RunnerWorkflowTerminalStatusHandler(
        IRunnerWorkflowStatusRouter router,
        ILogger<RunnerWorkflowTerminalStatusHandler> log)
    {
        _router = router;
        _log = log;
    }

    public bool Filter(CloudEvent evt) =>
        evt is not null &&
        TryResolve(evt.Type, out _);

    public async Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!TryResolve(evt.Type, out var status))
            return;

        var workflowRunId = CloudEventLineage.ReadValue(evt.Extensions, EventCatalog.Lineage.WorkflowRunId);
        if (string.IsNullOrWhiteSpace(workflowRunId))
        {
            _log.LogDebug(
                "Terminal status handler: cloud event {EventId} has no workflow run extension, skipping",
                evt.Id);
            return;
        }

        await _router.RouteAsync(workflowRunId, status, ct);
    }

    private static bool TryResolve(string? type, out WorkflowRunStatus status)
    {
        status = default;
        if (string.IsNullOrEmpty(type))
            return false;
        if (string.Equals(type, EventCatalog.ReverseDns.WorkflowRunCompleted, StringComparison.Ordinal))
        {
            status = WorkflowRunStatus.Completed;
            return true;
        }
        if (string.Equals(type, EventCatalog.ReverseDns.WorkflowRunStopped, StringComparison.Ordinal))
        {
            status = WorkflowRunStatus.Stopped;
            return true;
        }
        return false;
    }
}
