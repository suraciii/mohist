using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Events.Subscriptions;

/// <summary>
/// Server-side subscriber to workflow terminal lifecycle events. When a
/// workflow run reaches <see cref="WorkflowRunStatus.Completed"/>,
/// <see cref="WorkflowRunStatus.Stopped"/>, or
/// <see cref="WorkflowRunStatus.Failed"/>, this handler asks the runner
/// that owns the workspace to flip its local registry entry to
/// cleanup-eligible.
///
/// The server never performs filesystem deletion; it only delivers the
/// notification. The runner's convergence backstop
/// (POST /api/runner/{runnerId}/workflow-runs/status) catches missed
/// events when the SignalR push cannot be delivered (runner offline at
/// terminal moment, transient SignalR failure, etc.).
///
/// Subscriptions for the three terminal event types are registered as a
/// pipe-separated pattern so a single handler instance serves all three
/// types — the public CloudEvents bus wildcard syntax does not match
/// these three names with a single <c>.*</c> suffix.
///
/// The router call is fired on a detached background task: the bus
/// publishes from inside a workflow-grain call stack
/// (WorkflowRunStore.SaveAsync → IEventPublisher.PublishAsync →
/// in-process handlers), and the router then resolves the workflow
/// grain again. Running the router synchronously would self-deadlock a
/// non-reentrant workflow grain. Detaching preserves correctness
/// (push failures are logged, never propagated) and avoids blocking
/// the lifecycle commit.
/// </summary>
[Subscription(Type = "com.mohist.workflow.run.completed|com.mohist.workflow.run.stopped|com.mohist.workflow.run.failed")]
public sealed class RunnerWorkflowTerminalStatusHandler : ICloudEventHandler
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

    public Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!TryResolve(evt.Type, out var status))
            return Task.CompletedTask;

        var (workflowRunId, _) = WorkflowEventSerializer.ExtractContextFromSource(evt.Source.ToString());
        if (string.IsNullOrWhiteSpace(workflowRunId))
        {
            _log.LogDebug(
                "Terminal status handler: cloud event {EventId} has empty source, skipping",
                evt.Id);
            return Task.CompletedTask;
        }

        // Detach so the workflow-grain commit that triggered this event
        // returns immediately. The runner's convergence backstop is the
        // correctness backstop if the detached push fails.
        _ = RouteDetachedAsync(workflowRunId, status);
        return Task.CompletedTask;
    }

    private async Task RouteDetachedAsync(string workflowRunId, WorkflowRunStatus status)
    {
        try
        {
            await _router.RouteAsync(workflowRunId, status, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Terminal status handler: router failed for {WorkflowRunId} ({Status})",
                workflowRunId, status);
        }
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
        if (string.Equals(type, EventCatalog.ReverseDns.WorkflowRunFailed, StringComparison.Ordinal))
        {
            status = WorkflowRunStatus.Failed;
            return true;
        }
        return false;
    }
}