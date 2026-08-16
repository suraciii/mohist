using Mohist.Server.Agent.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Orleans;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Derives the public status of a Workflow-owned Agent invocation from the
/// AgentJob owner and the Workflow settlement facts. This is deliberately a
/// projection helper: no status mirror is persisted on the handoff plan or
/// TaskRun.
/// </summary>
public static class AgentInvocationStatusProjection
{
    public static string Map(
        AgentJobStatus agentJobStatus,
        bool recoveryPending = false,
        bool recoveryApplying = false) =>
        agentJobStatus == AgentJobStatus.Failed && (recoveryPending || recoveryApplying)
            ? "recovering"
            : agentJobStatus switch
            {
                AgentJobStatus.Pending => "queued",
                AgentJobStatus.Running => "executing",
                AgentJobStatus.Unknown => "executing",
                AgentJobStatus.Completed => "completed",
                AgentJobStatus.Failed => "failed",
                AgentJobStatus.Cancelled => "cancelled",
                _ => throw new ArgumentOutOfRangeException(nameof(agentJobStatus), agentJobStatus, null),
            };

    /// <summary>
    /// A failed terminal is recovering only while the durable finalizer still
    /// owns a recovery-capable receipt. Once the receipt is settled, the
    /// invocation exposes its AgentJob failure even when Workflow has moved
    /// on to a later recovery attempt.
    /// </summary>
    public static bool HasPendingRecoveryDecision(TaskRun task)
    {
        var receipt = task.AgentInvocationSettlement;
        if (receipt is null
            || receipt.IsSettled
            || receipt.Terminal.Status != AgentInvocationTerminalStatus.Failed
            || task.Recovery is not { Budget: > 0 })
        {
            return false;
        }

        return !task.RecoveryRemaining.HasValue || task.RecoveryRemaining.Value > 0;
    }
}

/// <summary>
/// Workflow read projection for one delegated Agent invocation. The result is
/// sourced from the AgentJob terminal record and is never assembled from
/// Session transcript parts.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowAgentInvocationView(
    [property: Id(0)] string InvocationId,
    [property: Id(1)] string WorkflowRunId,
    [property: Id(2)] string TaskRunId,
    [property: Id(3)] string WorkId,
    [property: Id(4)] string Status,
    [property: Id(5)] string JobId,
    [property: Id(6)] string SessionId,
    [property: Id(7)] string InputId,
    [property: Id(8)] string TurnId,
    [property: Id(9)] WorkflowAgentInvocationResultView? Result = null);

[GenerateSerializer]
public sealed record WorkflowAgentInvocationResultView(
    [property: Id(0)] string? Message,
    [property: Id(1)] string? Output,
    [property: Id(2)] IReadOnlyList<string>? ArtifactUploadIds,
    [property: Id(3)] string? FailureReason,
    [property: Id(4)] int? ExitCode);
