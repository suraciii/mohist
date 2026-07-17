using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Services;

public static class WorkflowStatusMapper
{
    public static string FrontendStatus(string raw) =>
        raw switch
        {
            "AwaitingApproval" => "awaiting-approval",
            _ => raw.ToLowerInvariant(),
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

        var effectiveFailure = run.EffectiveFailure();
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
                TaskRunExtensions.ExtractRequiredFiles(t.Expect),
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
