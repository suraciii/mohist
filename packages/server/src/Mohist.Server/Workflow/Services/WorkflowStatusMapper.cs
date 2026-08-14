using System.Runtime.CompilerServices;
using Mohist.Server.Infrastructure;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Services;

public static class WorkflowStatusMapper
{
    public const string AgentResultUnconfirmedReason = "agent-result-unconfirmed";
    public const string AgentResultSettlementNextAction =
        "Restore the original Runner and allow the result to replay, inspect the bound AgentSession and AgentTurn, or explicitly stop the workflow after confirming the physical target is no longer active.";
    public static readonly IReadOnlyList<string> AgentResultSettlementRecoveryActions = ["stop"];

    public static string WireStatus(WorkflowRunStatus status) => status switch
    {
        WorkflowRunStatus.Created => "created",
        WorkflowRunStatus.Pending => "pending",
        WorkflowRunStatus.Ready => "ready",
        WorkflowRunStatus.Running => "running",
        WorkflowRunStatus.AwaitingApproval => "awaiting-approval",
        WorkflowRunStatus.Paused => "paused",
        WorkflowRunStatus.Stopped => "stopped",
        WorkflowRunStatus.Completed => "completed",
        WorkflowRunStatus.Failed => "failed",
        _ => throw new SwitchExpressionException($"No wire mapping for WorkflowRunStatus value {status}"),
    };

    public static string WireStatus(StageRunStatus status) => status switch
    {
        StageRunStatus.Pending => "pending",
        StageRunStatus.Running => "running",
        StageRunStatus.AwaitingApproval => "awaiting-approval",
        StageRunStatus.Completed => "completed",
        StageRunStatus.Failed => "failed",
        _ => throw new SwitchExpressionException($"No wire mapping for StageRunStatus value {status}"),
    };

    public static string WireStatus(TaskRunStatus status) => status switch
    {
        TaskRunStatus.Pending => "pending",
        TaskRunStatus.Running => "running",
        TaskRunStatus.Completed => "completed",
        TaskRunStatus.Failed => "failed",
        TaskRunStatus.Cancelled => "cancelled",
        _ => throw new SwitchExpressionException($"No wire mapping for TaskRunStatus value {status}"),
    };

    public static string WireStatus(StageCheckStatus status) => status switch
    {
        StageCheckStatus.Pending => "pending",
        StageCheckStatus.Running => "running",
        StageCheckStatus.Passed => "passed",
        StageCheckStatus.Failed => "failed",
        _ => throw new SwitchExpressionException($"No wire mapping for StageCheckStatus value {status}"),
    };
    public static WorkflowStatusView? BuildStatusView(
        WorkflowRun? run,
        WorkflowDefinition? definition)
    {
        if (run is null) return null;

        var blocked = FindBlockedSettlement(run);

        var stages = run.Stages.Select((s, i) =>
        {
            var stageFailure = s.Failure is not null
                ? new FailureStatusView(
                    s.Failure.Reason.ToString(),
                    s.Failure.Stage,
                    s.Failure.TaskId,
                    s.Failure.CheckName,
                    s.Failure.Message,
                    s.Failure.Error)
                : null;

            return new StageStatusView(
                s.Id,
                DeriveStageStatus(s, blocked),
                i,
                MapTasks(s, definition),
                MapChecks(s, definition),
                s.ApprovalStatus is not null
                    ? new ApprovalStatusView(s.ApprovalStatus.Result, s.ApprovalStatus.RequestedAt, s.ApprovalStatus.RespondedAt, s.ApprovalStatus.DecidedBy, s.ApprovalStatus.DisplayName)
                    : null,
                stageFailure,
                MapFeedback(run, s.Id));
        }).ToList();

        var pending = BuildPendingWork(run);

        var effectiveFailure = run.EffectiveFailure();
        var failure = effectiveFailure is not null
            ? new FailureStatusView(
                effectiveFailure.Reason.ToString(),
                effectiveFailure.Stage,
                effectiveFailure.TaskId,
                effectiveFailure.CheckName,
                effectiveFailure.Message,
                effectiveFailure.Error)
            : null;

        var actions = BuildAvailableActions(run, effectiveFailure);
        if (blocked is not null)
            actions.Add(new AvailableActionView("stop", "Stop workflow", null));

        return new WorkflowStatusView(
            run.Id,
            blocked is not null ? "blocked" : WireStatus(run.Status),
            run.CurrentStageId,
            stages,
            pending,
            failure,
            actions,
            run.AssignedTo,
            run.Metadata is null ? null : new MetadataView(run.Metadata.Name, run.Metadata.Labels, run.Metadata.Annotations, run.Metadata.CreatedAt),
            MapAgentResultAttention(blocked));
    }

    /// <summary>
    /// The run's blocked wire status is derived from the settlement, never
    /// stored on the run: a blocked settlement is nonterminal attention, and
    /// the run's persisted status keeps its own lifecycle value.
    /// </summary>
    private static WorkflowAgentResultSettlementTask? FindBlockedSettlement(WorkflowRun run)
    {
        foreach (var stage in run.Stages)
        foreach (var task in stage.Tasks)
        {
            if (task.Status == TaskRunStatus.Running
                && task.AgentResultSettlement?.State == AgentResultSettlementState.Blocked)
            {
                return new WorkflowAgentResultSettlementTask(stage.Id, task);
            }
        }

        return null;
    }

    private static string DeriveTaskStatus(TaskRun task) =>
        task.Status == TaskRunStatus.Running
        && task.AgentResultSettlement?.State == AgentResultSettlementState.Blocked
            ? "blocked"
            : WireStatus(task.Status);

    private static string DeriveStageStatus(StageRun stage, WorkflowAgentResultSettlementTask? blocked) =>
        blocked is not null && blocked.Stage == stage.Id
            ? "blocked"
            : WireStatus(stage.Status);

    private static AgentResultAttentionView? MapAgentResultAttention(WorkflowAgentResultSettlementTask? blocked)
    {
        if (blocked is null) return null;
        var settlement = blocked.Task.AgentResultSettlement!;
        return new AgentResultAttentionView(
            "blocked",
            AgentResultUnconfirmedReason,
            settlement.Message ?? "Agent result was not confirmed before its deadline.",
            settlement.DeadlineAt ?? throw new InvalidOperationException("Blocked Agent result settlement requires a deadline."),
            settlement.TaskRunId,
            settlement.WorkId,
            settlement.RunnerId,
            settlement.AgentSessionId,
            settlement.AgentTurnId,
            AgentResultSettlementNextAction,
            AgentResultSettlementRecoveryActions);
    }

    private static AgentResultSettlementView? MapAgentResultSettlement(TaskRun task)
    {
        var settlement = task.AgentResultSettlement;
        if (settlement is null) return null;

        var blocked = settlement.State == AgentResultSettlementState.Blocked;
        return new AgentResultSettlementView(
            State: settlement.State switch
            {
                AgentResultSettlementState.AwaitingResult => "awaiting-result",
                AgentResultSettlementState.Unknown => "unknown",
                AgentResultSettlementState.Blocked => "blocked",
                _ => throw new SwitchExpressionException($"No settlement view mapping for {settlement.State}"),
            },
            Reason: blocked ? AgentResultUnconfirmedReason : settlement.ReasonCode,
            Message: settlement.Message,
            FirstUnknownAt: settlement.FirstUnknownAt,
            DeadlineAt: settlement.DeadlineAt,
            TaskRunId: settlement.TaskRunId,
            WorkId: settlement.WorkId,
            RunnerId: settlement.RunnerId,
            AgentSessionId: settlement.AgentSessionId,
            AgentTurnId: settlement.AgentTurnId,
            Runtime: settlement.Runtime,
            RuntimeSessionId: settlement.RuntimeSessionId,
            StopOperationId: settlement.StopOperationId,
            NextAction: settlement.State is AgentResultSettlementState.Unknown or AgentResultSettlementState.Blocked
                ? AgentResultSettlementNextAction
                : null,
            RecoveryActions: settlement.State is AgentResultSettlementState.Unknown or AgentResultSettlementState.Blocked
                ? AgentResultSettlementRecoveryActions
                : null);
    }

    public static List<StageFeedbackView> MapFeedback(WorkflowRun run, string stageId)
    {
        if (run.Feedback.Count == 0) return [];

        return run.Feedback
            .Where(f => string.Equals(f.Stage, stageId, StringComparison.Ordinal))
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new StageFeedbackView(
                f.Id,
                f.Body,
                f.Status,
                f.CreatedAt,
                ToResolution(f)))
            .ToList();
    }

    private static StageFeedbackResolution? ToResolution(ApprovalFeedback feedback) =>
        feedback.Status == ApprovalFeedbackStatus.Resolved
            ? new StageFeedbackResolution(
                feedback.ResolutionTaskId,
                feedback.ResolvedAt,
                feedback.ResolutionSummary)
            : null;

    public static List<TaskStatusView> MapTasks(StageRun stage, WorkflowDefinition? definition)
    {
        if (stage.Tasks.Count > 0)
            return stage.Tasks
                .Select(t => new TaskStatusView(
                    t.Id,
                    t.Title,
                    t.Uses,
                    DeriveTaskStatus(t),
                    t.RequiredFiles,
                    t.Classification,
                    TaskRunExtensions.ExtractSessionName(t.WithInput),
                    StartedAt: t.StartedAt,
                    CompletedAt: t.FinishedAt,
                    DurationMs: t.StartedAt is not null && t.FinishedAt is not null
                        ? (long)(t.FinishedAt.Value - t.StartedAt.Value).TotalMilliseconds
                        : null,
                    Output: MapTaskOutput(t.Output),
                    Error: t.Error,
                    AgentResultSettlement: MapAgentResultSettlement(t)))
                .ToList();

        var stageDefinition = definition?.Stages.FirstOrDefault(d => d.Stage == stage.Id);
        if (stageDefinition is null) return [];
        return stageDefinition.Tasks
            .Select(t => new TaskStatusView(
                t.Id,
                t.Title ?? t.Id,
                t.Uses,
                "pending",
                TaskRunExtensions.ExtractRequiredFiles(t.Expect),
                TaskRunExtensions.DeriveClassification(t.Uses, null),
                TaskRunExtensions.ExtractSessionName(t.With)))
            .ToList();
    }

    /// <summary>
    /// Project the stored task output into a view value. Historical
    /// non-object values (e.g. serialized JSON strings, scalars written by
    /// older runners) are normalized to <c>null</c> so the public API
    /// only ever exposes object-or-null for completed tasks. A successful
    /// task whose persisted output is a JSON object is returned as the
    /// object element so ASP.NET serializes it as a nested object.
    /// </summary>
    internal static System.Text.Json.JsonElement? MapTaskOutput(System.Text.Json.JsonElement? output)
    {
        if (!output.HasValue) return null;
        var element = output.Value;
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object) return element.Clone();
        return null;
    }

    public static List<CheckStatusView> MapChecks(StageRun stage, WorkflowDefinition? definition)
    {
        if (stage.Checks.Count > 0)
            return stage.Checks.Select(c => new CheckStatusView(c.Name, c.Title, c.Uses, WireStatus(c.Status), c.Message, c.Error)).ToList();

        var stageDefinition = definition?.Stages.FirstOrDefault(d => d.Stage == stage.Id);
        if (stageDefinition is null) return [];
        return stageDefinition.Checks
            .Select(c => new CheckStatusView(c.Id, c.Title ?? c.Id, c.Uses, "pending", null))
            .ToList();
    }

    public static List<AvailableActionView> BuildAvailableActions(WorkflowRun run, FailureDetails? failureOverride = null)
    {
        var actions = new List<AvailableActionView>();

        if (run.Status == WorkflowRunStatus.AwaitingApproval)
        {
            actions.Add(new AvailableActionView("approve", "Approve", null));
            actions.Add(new AvailableActionView("request-changes", "Request changes", null));
        }

        var failure = failureOverride ?? run.Failure;
        if (run.Status == WorkflowRunStatus.Failed && failure is not null)
        {
            var retry = run.RetryTarget(failure);
            if (retry is not null)
            {
                var title = retry.Reason is FailureReason.TaskFailed
                    ? "Retry failed task"
                    : "Retry failed check";
                actions.Add(new AvailableActionView("retry", title, retry.Target));
            }

            actions.Add(new AvailableActionView("rerun", "Rerun stage", run.CurrentStageId));
        }

        if (run.Status == WorkflowRunStatus.Paused)
        {
            actions.Add(new AvailableActionView("resume", "Resume", null));
        }

        if (run.Status == WorkflowRunStatus.Failed)
        {
            actions.Add(new AvailableActionView("start", "Start new workflow", null));
        }

        return actions;
    }

    public static PendingWorkView? BuildPendingWork(WorkflowRun run)
    {
        var pending = run.CurrentPendingWork();
        return pending is null
            ? null
            : new PendingWorkView(pending.Id, pending.WorkType, pending.Stage, pending.Title, null);
    }
}
