using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Orleans;

namespace Mohist.Server.Workflow.Subscriptions;

/// <summary>
/// Bus subscription that finalizes Workflow task completion from an
/// AgentJob terminal delivered over the typed workflow-terminal transport
/// (issue 559, design D5+D7). The AgentJob grain stages the durable
/// <c>PendingWorkflowTerminalDelivery</c> obligation and emits
/// <c>com.mohist.agent.job.workflow-terminal</c> with a stable event id;
/// this handler is the Workflow-owned consumer: it translates the typed
/// payload into an <see cref="AgentInvocationTerminal"/> and invokes
/// <see cref="IWorkflowGrain.SettleAgentInvocationAsync"/>, whose durable
/// per-effect receipts make the handler idempotent — duplicate deliveries,
/// dispatcher retries, and dead-letter redeliveries are all acknowledged
/// as already-applied from the receipt without reapplying effects. Agent
/// execution facts never ride the Workflow task-report endpoint.
/// </summary>
[Subscription(
    Type = EventCatalog.ReverseDns.AgentJobWorkflowTerminal,
    Identity = "Mohist.Server.Workflow.Subscriptions.AgentJobWorkflowTerminalHandler")]
public sealed class AgentJobWorkflowTerminalHandler : ICloudEventHandler
{
    private readonly IGrainFactory _grains;
    private readonly ILogger<AgentJobWorkflowTerminalHandler> _log;

    public AgentJobWorkflowTerminalHandler(
        IGrainFactory grains,
        ILogger<AgentJobWorkflowTerminalHandler> log)
    {
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent evt) => evt.Data is { ValueKind: JsonValueKind.Object };

    public async Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var payload = evt.Data?.Deserialize<WorkflowTerminalEventData>(CloudEvent.JsonOptions)
            ?? throw new InvalidOperationException("Workflow terminal event has no valid payload.");
        payload.Validate();

        var ack = await _grains
            .GetGrain<IWorkflowGrain>(payload.WorkflowRunId)
            .SettleAgentInvocationAsync(payload.ToTerminal(evt.Id));

        if (ack != AgentInvocationSettlementAck.Applied)
        {
            _log.LogDebug(
                "Workflow terminal {EventId} for invocation {InvocationId} acknowledged as {Ack}",
                evt.Id, payload.InvocationId, ack);
        }
    }
}

/// <summary>
/// Wire shape of the <c>com.mohist.agent.job.workflow-terminal</c> payload
/// (built by <c>AgentJobLineage.BuildWorkflowTerminalEnvelope</c>): the
/// invocation identity, the terminal facts, the boundary completion
/// evaluation, and the recorded timestamp. Null facts are omitted by the
/// producer's serializer, so optional members are nullable.
/// </summary>
public sealed record WorkflowTerminalEventData(
    string InvocationId,
    string? ProjectId,
    string WorkflowRunId,
    string TaskRunId,
    string WorkId,
    string JobId,
    string? SessionId,
    string? InputId,
    string? TurnId,
    string Status,
    JsonElement? Output,
    string? Message,
    string? FailureReason,
    string? FailureCategory,
    int? ExitCode,
    string[]? ArtifactUploadIds,
    WorkflowTerminalEvaluationData? Evaluation,
    DateTimeOffset RecordedAt)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(InvocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkflowRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(TaskRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(JobId);
        if (Status is not ("completed" or "failed" or "cancelled"))
            throw new InvalidOperationException($"Invalid workflow terminal status '{Status}'.");
    }

    public AgentInvocationTerminal ToTerminal(string deliveryId) => new(
        DeliveryId: deliveryId,
        InvocationId: InvocationId,
        ProjectId: ProjectId ?? string.Empty,
        WorkflowRunId: WorkflowRunId,
        TaskRunId: TaskRunId,
        WorkId: WorkId,
        JobId: JobId,
        SessionId: SessionId,
        Status: Status switch
        {
            "completed" => AgentInvocationTerminalStatus.Completed,
            "cancelled" => AgentInvocationTerminalStatus.Cancelled,
            _ => AgentInvocationTerminalStatus.Failed,
        },
        Message: Message,
        FailureReason: FailureReason,
        FailureCategory: FailureCategory,
        ExitCode: ExitCode,
        ArtifactUploadIds: ArtifactUploadIds,
        Expectation: Evaluation is null
            ? null
            : new AgentInvocationExpectation(
                Evaluation.Satisfied,
                Evaluation.Matched,
                Evaluation.Message),
        RecordedAt: RecordedAt,
        Output: Output,
        InputId: InputId,
        TurnId: TurnId);
}

public sealed record WorkflowTerminalEvaluationData(
    bool Satisfied,
    string? Matched,
    object? MissingFiles,
    object? MissingMarkers,
    object? FailIfMatches,
    string? Message);
