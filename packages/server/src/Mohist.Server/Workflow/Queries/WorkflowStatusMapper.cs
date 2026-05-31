using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Views;

namespace Mohist.Server.Workflow.Queries;

public static class WorkflowStatusMapper
{
    public static WorkflowStatusView? BuildStatusView(
        WorkflowRun? run,
        WorkflowRunProfile? profile,
        WorkLease? lease)
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
                s.Status.ToString(),
                i,
                MapTasks(s, profile),
                MapChecks(s, profile),
                s.ApprovalStatus is not null
                    ? new ApprovalStatusView(s.ApprovalStatus.Result, s.ApprovalStatus.RequestedAt, s.ApprovalStatus.RespondedAt)
                    : null,
                stageFailure);
        }).ToList();

        var pending = lease is not null
            ? new PendingWorkView(lease.WorkId, lease.WorkType, lease.Stage, lease.Title, null)
            : null;

        var failure = run.Failure is not null
            ? new FailureStatusView(
                run.Failure.Reason.ToString(),
                run.Failure.Stage,
                run.Failure.TaskId,
                run.Failure.CheckName,
                run.Failure.Message)
            : null;

        var actions = BuildAvailableActions(run);

        return new WorkflowStatusView(
            run.Id,
            run.Status.ToString(),
            run.CurrentStageId,
            stages,
            pending,
            failure,
            actions,
            run.Metadata is null ? null : new MetadataView(run.Metadata.Name, run.Metadata.Labels, run.Metadata.Annotations, run.Metadata.CreatedAt));
    }

    public static List<TaskStatusView> MapTasks(StageRun stage, WorkflowRunProfile? profile)
    {
        if (stage.Tasks.Count > 0)
            return stage.Tasks.Select(t => new TaskStatusView(t.Id, t.Title, t.Uses, t.Status.ToString())).ToList();

        var definition = profile?.Definition.Stages.FirstOrDefault(d => d.Stage == stage.Id);
        if (definition is null) return [];
        return definition.Tasks
            .Select(t => new TaskStatusView(t.Id, t.Title, t.Uses, "Pending"))
            .ToList();
    }

    public static List<CheckStatusView> MapChecks(StageRun stage, WorkflowRunProfile? profile)
    {
        if (stage.Checks.Count > 0)
            return stage.Checks.Select(c => new CheckStatusView(c.Name, c.Title, c.Uses, c.Status.ToString(), c.Message)).ToList();

        var definition = profile?.Definition.Stages.FirstOrDefault(d => d.Stage == stage.Id);
        if (definition is null) return [];
        return definition.Checks
            .Select(c => new CheckStatusView(c.Name, c.Title, c.Uses, "Pending", null))
            .ToList();
    }

    public static List<AvailableActionView> BuildAvailableActions(WorkflowRun run)
    {
        var actions = new List<AvailableActionView>();

        if (run.Status == WorkflowRunStatus.AwaitingApproval)
        {
            actions.Add(new AvailableActionView("approve", "Approve", null));
            actions.Add(new AvailableActionView("reject", "Reject", null));
        }

        if (run.Status == WorkflowRunStatus.Failed && run.Failure is not null)
        {
            var failure = run.Failure;
            if (failure.Reason is FailureReason.TaskFailed && failure.TaskId is not null)
            {
                actions.Add(new AvailableActionView("retry", "Retry failed task", failure.TaskId));
            }
            else if (failure.Reason is FailureReason.CheckUnrepaired && failure.CheckName is not null)
            {
                actions.Add(new AvailableActionView("retry", "Retry failed check", failure.CheckName));
            }

            actions.Add(new AvailableActionView("rerun", "Rerun stage", run.CurrentStageId));
        }

        return actions;
    }
}
