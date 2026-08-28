using System.Runtime.CompilerServices;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Services;

public static class WorkflowStatusMapper
{
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

    public static string WireStatus(WorkflowActionAttemptStatus status) => status switch
    {
        WorkflowActionAttemptStatus.Pending => "pending",
        WorkflowActionAttemptStatus.Running => "running",
        WorkflowActionAttemptStatus.Completed => "completed",
        WorkflowActionAttemptStatus.Failed => "failed",
        WorkflowActionAttemptStatus.Cancelled => "cancelled",
        _ => throw new SwitchExpressionException($"No wire mapping for WorkflowActionAttemptStatus value {status}"),
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
                WireStatus(s.Status),
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
        return new WorkflowStatusView(
            run.Id,
            WireStatus(run.Status),
            run.CurrentStageId,
            stages,
            pending,
            failure,
            actions,
            run.AssignedTo,
            run.Metadata is null ? null : new MetadataView(run.Metadata.Name, run.Metadata.Labels, run.Metadata.Annotations, run.Metadata.CreatedAt),
            MapVerificationLanes(run));
    }

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
                    attempt.ActionAttemptId,
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
                    ActionAttemptId: string.Empty));
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
                    budgets.TryAdd(task.Id, WorkflowActionAttemptExtensions.TryGetConfiguredBudgetMs(task.With));
            }
            return budgets;
        }
        catch
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    public static TaskLaneView? MapTaskLane(WorkflowActionAttempt task)
    {
        if (task.Lane is null) return null;
        return new TaskLaneView(
            task.Lane.LaneId,
            task.Lane.Order,
            task.Lane.ConfiguredBudgetMs,
            task.Lane.Outcome.WireValue(),
            task.Lane.ActionAttemptId,
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
                    WireStatus(t.Status),
                    t.RequiredFiles,
                    t.Classification,
                    WorkflowActionAttemptExtensions.ExtractSessionName(t.WithInput),
                    StartedAt: t.StartedAt,
                    CompletedAt: t.FinishedAt,
                    DurationMs: t.StartedAt is not null && t.FinishedAt is not null
                        ? (long)(t.FinishedAt.Value - t.StartedAt.Value).TotalMilliseconds
                        : null,
                    Output: MapTaskOutput(t.Output),
                    Error: t.Error,
                    Lane: MapTaskLane(t),
                    AgentJobId: t.AgentJobId,
                    AgentSessionId: t.AgentSessionId))
                .ToList();

        var stageDefinition = definition?.Stages.FirstOrDefault(d => d.Stage == stage.Id);
        if (stageDefinition is null) return [];
        return stageDefinition.Tasks
            .Select(t => new TaskStatusView(
                t.Id,
                t.Title ?? t.Id,
                t.Uses,
                "pending",
                WorkflowActionAttemptExtensions.ExtractRequiredFiles(t.Expect),
                WorkflowActionAttemptExtensions.DeriveClassification(t.Uses, null),
                WorkflowActionAttemptExtensions.ExtractSessionName(t.With)))
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
            return stage.Checks
                .Select(c => new CheckStatusView(
                    c.Name,
                    c.Title,
                    c.Uses,
                    WireStatus(c.Status),
                    c.Message,
                    c.Error))
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
