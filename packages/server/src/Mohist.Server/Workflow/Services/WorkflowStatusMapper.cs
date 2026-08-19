using System.Runtime.CompilerServices;
using Mohist.Server.Contracts;
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
        TaskRunStatus.Interrupted => "interrupted",
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
        var interruption = FindCurrentInterruption(run);
        var agentInterruption = FindInterruption(run);

        var stages = run.Stages.Select((s, i) =>
        {
            var stageInterruption = FindStageInterruption(s);
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
                DeriveStageStatus(s, blocked, stageInterruption),
                i,
                MapTasks(s, definition),
                MapChecks(s, definition),
                s.ApprovalStatus is not null
                    ? new ApprovalStatusView(s.ApprovalStatus.Result, s.ApprovalStatus.RequestedAt, s.ApprovalStatus.RespondedAt, s.ApprovalStatus.DecidedBy, s.ApprovalStatus.DisplayName)
                    : null,
                stageFailure,
                MapFeedback(run, s.Id),
                MapInterruption(stageInterruption));
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
            blocked is not null
                ? "blocked"
                : interruption is not null ? "recoverable-interrupted" : WireStatus(run.Status),
            run.CurrentStageId,
            stages,
            pending,
            failure,
            actions,
            run.AssignedTo,
            run.Metadata is null ? null : new MetadataView(run.Metadata.Name, run.Metadata.Labels, run.Metadata.Annotations, run.Metadata.CreatedAt),
            MapAgentResultAttention(blocked),
            MapInterruption(interruption),
            MapInterruptionAttention(agentInterruption),
            MapVerificationLanes(run));
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
            : task.Status == TaskRunStatus.Running && task.Interruption is not null
                ? "recoverable-interrupted"
                : WireStatus(task.Status);

    private static string DeriveStageStatus(
        StageRun stage,
        WorkflowAgentResultSettlementTask? blocked,
        WorkInterruption? interruption) =>
        blocked is not null && blocked.Stage == stage.Id
            ? "blocked"
            : stage.Status == StageRunStatus.Running && interruption is not null
                ? "recoverable-interrupted"
                : WireStatus(stage.Status);

    private static WorkInterruption? FindCurrentInterruption(WorkflowRun run)
    {
        if (run.CurrentStageId is not { } currentStageId)
            return null;

        var current = run.Stages.FirstOrDefault(stage => stage.Id == currentStageId);
        return current?.RunningTask?.Interruption ?? current?.Interruption;
    }

    private static WorkInterruption? FindStageInterruption(StageRun stage) =>
        stage.Interruption
        ?? stage.Tasks.FirstOrDefault(task => task.Status == TaskRunStatus.Running)?.Interruption;

    private static WorkInterruptionView? MapInterruption(WorkInterruption? interruption) =>
        interruption is null
            ? null
            : new WorkInterruptionView(
                interruption.ReasonCode,
                interruption.WorkId,
                interruption.OwnerId,
                interruption.RecordedAt,
                interruption.RecoveryDeadlineAt);

    private static AgentResultAttentionView? MapAgentResultAttention(WorkflowAgentResultSettlementTask? blocked)
    {
        if (blocked is null) return null;
        var settlement = blocked.Task.AgentResultSettlement!;
        var interruption = MapInterruption(blocked.Task.AgentInterruption);
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
            settlement.UpdateOperationId,
            interruption?.ExpectedRecoveryPath,
            interruption?.StopFailure,
            AgentResultSettlementNextAction,
            AgentResultSettlementRecoveryActions,
            ReasonCode: settlement.ReasonCode);
    }

    private static AgentResultSettlementView? MapAgentResultSettlement(TaskRun task)
    {
        var settlement = task.AgentResultSettlement;
        if (settlement is null) return null;

        var blocked = settlement.State == AgentResultSettlementState.Blocked;
        var interruption = MapInterruption(task.Interruption);
        return new AgentResultSettlementView(
            State: settlement.State switch
            {
                AgentResultSettlementState.AwaitingResult => "awaiting-result",
                AgentResultSettlementState.Unknown => "unknown",
                AgentResultSettlementState.Blocked => "blocked",
                AgentResultSettlementState.RecoverablyInterrupted => "interrupted",
                _ => throw new SwitchExpressionException($"No settlement view mapping for {settlement.State}"),
            },
            Reason: blocked ? AgentResultUnconfirmedReason : settlement.ReasonCode,
            ReasonCode: settlement.ReasonCode,
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
            UpdateOperationId: settlement.UpdateOperationId,
            ExpectedRecoveryPath: MapInterruption(task.AgentInterruption)?.ExpectedRecoveryPath,
            StopFailure: MapInterruption(task.AgentInterruption)?.StopFailure,
            Interruption: MapInterruption(task.AgentInterruption),
            NextAction: settlement.State is AgentResultSettlementState.Unknown or AgentResultSettlementState.Blocked
                ? AgentResultSettlementNextAction
                : null,
            RecoveryActions: settlement.State is AgentResultSettlementState.Unknown or AgentResultSettlementState.Blocked
                ? AgentResultSettlementRecoveryActions
                : null);
    }

    private static WorkflowAgentResultSettlementTask? FindInterruption(WorkflowRun run)
    {
        var candidates = run.Stages
            .SelectMany(stage => stage.Tasks.Select(task => new WorkflowAgentResultSettlementTask(stage.Id, task)))
            .Where(candidate => candidate.Task.AgentInterruption is not null)
            .OrderByDescending(candidate => candidate.Task.AgentInterruption!.RecoveryGeneration)
            .ThenByDescending(candidate => AgentWorkInterruptionProjection.Rank(candidate.Task.AgentInterruption!.State));
        return candidates.FirstOrDefault();
    }

    private static AgentInterruptionAttentionView? MapInterruptionAttention(
        WorkflowAgentResultSettlementTask? interruption)
    {
        var visibility = interruption is null ? null : MapInterruption(interruption.Task.AgentInterruption);
        if (visibility is null) return null;
        return new AgentInterruptionAttentionView(
            visibility.State,
            visibility.State == AgentWorkInterruptionStates.Recovered
                ? "The replacement Agent execution recovered after the Runner update."
                : "Agent work was interrupted by a Runner update and is following the recorded recovery path.",
            visibility.UpdateOperationId,
            visibility.WorkId,
            visibility.TaskRunId,
            visibility.RecoveryGeneration,
            interruption!.Task.AgentResultSettlement?.AgentSessionId,
            visibility.OriginalTurnId,
            visibility.ReplacementTurnId,
            visibility.ExpectedRecoveryPath,
            visibility.StopFailure);
    }

    private static AgentWorkInterruptionView? MapInterruption(
        AgentWorkInterruptionTransition? transition) =>
        transition is null
            ? null
            : new AgentWorkInterruptionView(
                transition.State,
                transition.UpdateOperationId,
                transition.WorkId,
                transition.TaskRunId,
                transition.RecoveryGeneration,
                transition.OriginalTurnId,
                transition.ReplacementTurnId,
                AgentWorkInterruptionProjection.SanitizeStopFailure(transition.StopFailure),
                transition.ExpectedRecoveryPath,
                transition.RecordedAt);

    /// <summary>
    /// Build the verification-lane projection. Returns <c>null</c> for
    /// runs that are not lane-enabled so legacy aggregate state remains
    /// readable and is not asked to wait for synthesized lanes. For
    /// lane-enabled runs the projection always contains one entry per
    /// catalog lane (in catalog order), filling in pending placeholders
    /// for lanes that have not yet reported.
    /// </summary>
    public static VerificationLanesView? MapVerificationLanes(WorkflowRun run)
    {
        if (!VerificationLaneGate.IsLaneEnabledRun(run)) return null;

        var byLaneId = VerificationLaneGate.AuthoritativeLaneAttempts(run);
        var configuredBudgets = BoundLaneBudgets(run);
        var ordered = new List<VerificationLaneView>(VerificationLaneCatalog.LaneIds.Count);
        string? firstNonPassing = null;
        foreach (var laneId in VerificationLaneCatalog.LaneIds)
        {
            var order = VerificationLaneCatalog.OrderOf(laneId);
            if (byLaneId.TryGetValue(laneId, out var attempt))
            {
                ordered.Add(new VerificationLaneView(
                    laneId,
                    attempt.Order,
                    attempt.ConfiguredBudgetMs,
                    attempt.Outcome.WireValue(),
                    attempt.TaskRunId,
                    attempt.WorkId,
                    attempt.Detail,
                    attempt.Error,
                    attempt.FinishedAt));
                if (firstNonPassing is null && attempt.Outcome != VerificationLaneOutcome.Pass)
                    firstNonPassing = laneId;
            }
            else
            {
                // Pending placeholder preserves the lane's catalog order and
                // configured budget from the bound definition so downstream
                // consumers can render a complete six-lane summary even before
                // every lane attempt materializes.
                ordered.Add(new VerificationLaneView(
                    laneId,
                    order,
                    configuredBudgets.TryGetValue(laneId, out var budget) ? budget : 0,
                    Outcome: VerificationLaneOutcome.Pending.WireValue(),
                    TaskRunId: string.Empty));
                firstNonPassing ??= laneId;
            }
        }

        return new VerificationLanesView(
            AllPassing: firstNonPassing is null,
            FirstNonPassingLane: firstNonPassing,
            Lanes: ordered);
    }

    private static IReadOnlyDictionary<string, int> BoundLaneBudgets(WorkflowRun run)
    {
        if (string.IsNullOrWhiteSpace(run.BoundWorkflowDefinitionJson))
            return new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            var definition = WorkflowYamlSerializer.FromJson(run.BoundWorkflowDefinitionJson);
            var build = definition.Stages.FirstOrDefault(stage =>
                string.Equals(stage.Stage, "build", StringComparison.Ordinal));
            if (build is null) return new Dictionary<string, int>(StringComparer.Ordinal);

            var budgets = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var task in build.Tasks)
            {
                if (VerificationLaneCatalog.IsKnownLane(task.Id))
                    budgets.TryAdd(task.Id, TaskRunExtensions.TryGetConfiguredBudgetMs(task.With));
            }
            return budgets;
        }
        catch
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    public static TaskLaneView? MapTaskLane(TaskRun task)
    {
        if (task.Lane is null) return null;
        return new TaskLaneView(
            task.Lane.LaneId,
            task.Lane.Order,
            task.Lane.ConfiguredBudgetMs,
            task.Lane.Outcome.WireValue(),
            task.Lane.TaskRunId,
            task.Lane.WorkId,
            task.Lane.Detail,
            task.Lane.Error,
            task.Lane.FinishedAt);
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
                    AgentResultSettlement: MapAgentResultSettlement(t),
                    Interruption: MapInterruption(t.Interruption),
                    Lane: MapTaskLane(t)))
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
                TaskRunExtensions.ExtractSessionName(t.With),
                Interruption: null))
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
        {
            var interruption = MapInterruption(stage.Interruption);
            return stage.Checks
                .Select(c => new CheckStatusView(
                    c.Name,
                    c.Title,
                    c.Uses,
                    c.Status == StageCheckStatus.Running && interruption is not null
                        ? "recoverable-interrupted"
                        : WireStatus(c.Status),
                    c.Message,
                    c.Error,
                    interruption))
                .ToList();
        }

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
