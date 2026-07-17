using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Events.Subscriptions;

/// <summary>
/// Bus subscription that releases the workflow run's sequential stage lock
/// when a <c>StageCompleted</c> or <c>StageFailed</c> event row is
/// persisted.
///
/// Replaces the previous grain-internal <c>WorkflowGrain.On()</c> branch
/// (<c>StageCompleted/Failed =&gt; ReleaseStageLocksAsync</c>) so the lock
/// release flows through the same event path as <c>WorkflowRunStopped</c>
/// already does. The grain still owns the lock-release logic; this handler
/// only translates <see cref="CloudEvent"/>s back into grain calls and
/// resolves the workflow run id from the CloudEvent source URI
/// (<c>/mohist/workflow-runs/{id}</c>).
///
/// The durable dispatcher awaits this handler's <see cref="ICloudEventHandler.HandleAsync"/>
/// invocation. The handler resolves the target <see cref="IWorkflowGrain"/> and
/// calls <c>ReleaseStageLocksAsync</c> on the await stack. Failures escape
/// into the durable dispatcher's retry / dead-letter pipeline.
/// </summary>
[Subscription(Type = "com.mohist.workflow.stage.completed|com.mohist.workflow.stage.failed")]
public sealed class WorkflowStageLockReleaseHandler : ICloudEventHandler
{
    private readonly IGrainFactory _grains;
    private readonly ILogger<WorkflowStageLockReleaseHandler> _log;

    public WorkflowStageLockReleaseHandler(
        IGrainFactory grains,
        ILogger<WorkflowStageLockReleaseHandler> log)
    {
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent evt) => true;

    public async Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        var workflowRunId = CloudEventLineage.ReadValue(evt.Extensions, EventCatalog.Lineage.WorkflowRunId);
        if (string.IsNullOrEmpty(workflowRunId))
        {
            _log.LogDebug(
                "Stage lock release skipped: event {EventId} has no workflow run extension",
                evt.Id);
            return;
        }

        var stage = CloudEventLineage.ReadValue(evt.Extensions, EventCatalog.Lineage.Stage);
        if (string.IsNullOrEmpty(stage))
        {
            _log.LogDebug(
                "Stage lock release skipped: event {EventId} for workflow {WorkflowRunId} has no stage",
                evt.Id, workflowRunId);
            return;
        }

        var reason = evt.Type == EventCatalog.ReverseDns.StageFailed ? "failed" : "completed";
        var grain = _grains.GetGrain<IWorkflowGrain>(workflowRunId);
        await grain.ReleaseStageLocksAsync(stage, reason).ConfigureAwait(false);
    }

}
