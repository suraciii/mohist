using System.Text.Json;
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
        var workflowRunId = ExtractWorkflowRunId(evt.Source.ToString());
        if (string.IsNullOrEmpty(workflowRunId))
        {
            _log.LogDebug(
                "Stage lock release skipped: event {EventId} source {Source} does not carry a workflow run id",
                evt.Id, evt.Source);
            return;
        }

        var stage = ExtractStage(evt.Data);
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

    internal static string ExtractWorkflowRunId(string source)
    {
        const string prefix = "/mohist/workflow-runs/";
        return source.StartsWith(prefix, StringComparison.Ordinal)
            ? source[prefix.Length..]
            : string.Empty;
    }

    internal static string? ExtractStage(JsonElement? data)
    {
        if (data is null || !data.HasValue) return null;
        var value = data.Value;
        if (value.ValueKind != JsonValueKind.Object) return null;

        // WorkflowEvent is a union type (C# preview `union` feature) — its
        // serialized form wraps the active case in a "value" envelope:
        //   {"value":{"stage":"build"}} for StageCompleted
        //   {"value":{"stage":"integrate","reason":"..."}} for StageFailed
        // Unwrap the envelope before reading the case's properties. We also
        // accept the bare {"stage":"..."} shape so handlers stay tolerant
        // to direct serializations of the case types themselves.
        JsonElement inner = value;
        if (value.TryGetProperty("value", out var wrapped)
            && wrapped.ValueKind == JsonValueKind.Object)
        {
            inner = wrapped;
        }

        if (inner.TryGetProperty("stage", out var lower) && lower.ValueKind == JsonValueKind.String)
            return lower.GetString();
        if (inner.TryGetProperty("Stage", out var upper) && upper.ValueKind == JsonValueKind.String)
            return upper.GetString();
        return null;
    }
}
