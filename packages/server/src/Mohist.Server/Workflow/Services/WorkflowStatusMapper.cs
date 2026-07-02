using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Services;

public static class WorkflowStatusMapper
{
    public static string FrontendStatus(string raw) =>
        raw.Equals("AwaitingApproval", StringComparison.Ordinal)
            ? "awaiting-approval"
            : raw.ToLowerInvariant();
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
                    s.Failure.Message)
                : null;

            return new StageStatusView(
                s.Id,
                FrontendStatus(s.Status.ToString()),
                i,
                MapTasks(s, definition),
                MapChecks(s, definition),
                s.ApprovalStatus is not null
                    ? new ApprovalStatusView(s.ApprovalStatus.Result, s.ApprovalStatus.RequestedAt, s.ApprovalStatus.RespondedAt)
                    : null,
                stageFailure,
                MapFeedback(run, s.Id));
        }).ToList();

        var pending = BuildPendingWork(run);

        var effectiveFailure = run.Failure ?? CurrentStageFailure(run);
        var failure = effectiveFailure is not null
            ? new FailureStatusView(
                effectiveFailure.Reason.ToString(),
                effectiveFailure.Stage,
                effectiveFailure.TaskId,
                effectiveFailure.CheckName,
                effectiveFailure.Message)
            : null;

        var actions = BuildAvailableActions(run, effectiveFailure);

        return new WorkflowStatusView(
            run.Id,
            FrontendStatus(run.Status.ToString()),
            run.CurrentStageId,
            stages,
            pending,
            failure,
            actions,
            run.AssignedTo,
            run.Metadata is null ? null : new MetadataView(run.Metadata.Name, run.Metadata.Labels, run.Metadata.Annotations, run.Metadata.CreatedAt));
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
                    FrontendStatus(t.Status.ToString()),
                    t.RequiredFiles,
                    t.Classification,
                    TaskRunExtensions.ExtractSessionName(t.WithInput),
                    StartedAt: t.StartedAt,
                    CompletedAt: t.FinishedAt,
                    DurationMs: t.StartedAt is not null && t.FinishedAt is not null
                        ? (long)(t.FinishedAt.Value - t.StartedAt.Value).TotalMilliseconds
                        : null,
                    Output: t.Output.HasValue ? JSON.Serialize(t.Output.Value) : null))
                .ToList();

        var stageDefinition = definition?.Stages.FirstOrDefault(d => d.Stage == stage.Id);
        if (stageDefinition is null) return [];
        return stageDefinition.Tasks
            .Select(t => new TaskStatusView(
                t.Id,
                t.Title,
                t.Uses,
                "pending",
                TaskRunExtensions.ExtractRequiredFiles(t.With),
                TaskRunExtensions.DeriveClassification(t.Uses, null),
                TaskRunExtensions.ExtractSessionName(t.With)))
            .ToList();
    }

    public static List<CheckStatusView> MapChecks(StageRun stage, WorkflowDefinition? definition)
    {
        if (stage.Checks.Count > 0)
            return stage.Checks.Select(c => new CheckStatusView(c.Name, c.Title, c.Uses, FrontendStatus(c.Status.ToString()), c.Message)).ToList();

        var stageDefinition = definition?.Stages.FirstOrDefault(d => d.Stage == stage.Id);
        if (stageDefinition is null) return [];
        return stageDefinition.Checks
            .Select(c => new CheckStatusView(c.Name, c.Title, c.Uses, "pending", null))
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
            var taskId = failure.TaskId;
            if (failure.Reason is FailureReason.TaskFailed)
            {
                if (taskId is null && failure.Stage is not null)
                {
                    var failedStage = run.Stages.FirstOrDefault(s => s.Id == failure.Stage);
                    taskId = failedStage?.Tasks.LastOrDefault(t => t.Status == TaskRunStatus.Failed)?.Id;
                }

                if (taskId is not null)
                {
                    actions.Add(new AvailableActionView("retry", "Retry failed task", taskId));
                }
            }
            else if (failure.Reason is FailureReason.CheckUnrepaired && failure.CheckName is not null)
            {
                actions.Add(new AvailableActionView("retry", "Repair failed check", failure.CheckName));
            }
            else if (failure.Reason is FailureReason.ContextExhaustion)
            {
                actions.Add(new AvailableActionView("compact", "Compact session", failure.Stage));
                actions.Add(new AvailableActionView("reset", "Reset session", failure.Stage));
            }

            actions.Add(new AvailableActionView("rerun", "Rerun stage", run.CurrentStageId));
        }

        if (run.Status == WorkflowRunStatus.Failed)
        {
            actions.Add(new AvailableActionView("start", "Start new workflow", null));
        }

        return actions;
    }

    public static PendingWorkView? BuildPendingWork(WorkflowRun run)
    {
        if (run.CurrentStageId is null) return null;
        if (run.Status is not (WorkflowRunStatus.Ready or WorkflowRunStatus.Running)) return null;
        var stage = run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId);
        if (stage is null) return null;

        var pendingTask = stage.Tasks.FirstOrDefault(t => t.Status is not (TaskRunStatus.Completed or TaskRunStatus.Failed));
        if (pendingTask is not null)
            return new PendingWorkView(pendingTask.Id, "task", stage.Id, pendingTask.Title, null);

        if (stage.Checks.Count > 0 && stage.Checks.Any(c => c.Status != StageCheckStatus.Passed))
            return new PendingWorkView("checks", "checks", stage.Id, "Checks", null);

        return null;
    }

    private static FailureDetails? CurrentStageFailure(WorkflowRun run)
        => run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId)?.Failure;
}
