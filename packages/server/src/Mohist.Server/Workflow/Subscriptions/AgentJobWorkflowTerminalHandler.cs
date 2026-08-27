using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Orleans;

namespace Mohist.Server.Workflow.Subscriptions;

[Subscription(
    Type = EventCatalog.ReverseDns.AgentJobWorkflowTerminal,
    Identity = "Mohist.Server.Workflow.Subscriptions.AgentJobWorkflowTerminalHandler")]
public sealed class AgentJobWorkflowTerminalHandler(IGrainFactory grains) : ICloudEventHandler
{
    public bool Filter(CloudEvent evt) => evt.Data is { ValueKind: JsonValueKind.Object };

    public async Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var data = evt.Data?.Deserialize<AgentJobWorkflowTerminalEventData>(CloudEvent.JsonOptions)
            ?? throw new InvalidOperationException("Workflow Agent terminal event has no valid payload.");
        data.Validate();
        var verdict = await grains.GetGrain<IWorkflowGrain>(data.WorkflowRunId)
            .ReceiveAgentJobTerminalAsync(new WorkflowAgentJobTerminalDelivery(
                evt.Id,
                data.JobKey,
                data.InvocationId,
                data.CommandId,
                data.ActionAttemptId,
                data.WorkId,
                data.Stage,
                data.RequestFingerprint,
                data.Status,
                data.Message,
                data.Output,
                data.ArtifactUploadIds,
                data.FailureReason,
                data.FailureCategory,
                data.ExitCode,
                data.ResultFingerprint,
                data.AgentSessionId,
                data.InitialInputId,
                data.InitialTurnId,
                string.IsNullOrWhiteSpace(data.AddTasksJson)
                    ? null
                    : Mohist.Server.Infrastructure.JSON.Deserialize<List<RuntimeTaskInput>>(data.AddTasksJson)));
        if (verdict == WorkReportVerdict.Outstanding)
            throw new InvalidOperationException($"Workflow '{data.WorkflowRunId}' has not persisted AgentJob terminal delivery '{evt.Id}'.");
    }
}

public sealed record AgentJobWorkflowTerminalEventData(
    string JobKey,
    string InvocationId,
    string CommandId,
    string WorkflowRunId,
    string ActionAttemptId,
    string WorkId,
    string Stage,
    string RequestFingerprint,
    string Status,
    string? Message,
    string? Output,
    string[]? ArtifactUploadIds,
    string? FailureReason,
    string? FailureCategory,
    int? ExitCode,
    string? ResultFingerprint,
    string? AgentSessionId,
    string? InitialInputId,
    string? InitialTurnId,
    string? AddTasksJson)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(JobKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(InvocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(CommandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkflowRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ActionAttemptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(RequestFingerprint);
        if (Status is not ("completed" or "failed" or "cancelled" or "interrupted"))
            throw new InvalidOperationException($"Invalid Workflow Agent terminal status '{Status}'.");
    }
}
