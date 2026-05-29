using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Workflow.Projection;

public static class WorkflowStatusReader
{
    public static WorkflowStatusSnapshot? Read(WorkflowRunState state, WorkLease? lease)
    {
        var run = state.Run;

        var stages = run.Stages.Select(stage =>
            new StageStatusSnapshot(
                stage.Stage,
                StageStatus(stage),
                stage.Order,
                stage.Tasks.Select(task => new TaskStatusSnapshot(
                    task.DefinitionId,
                    task.Title,
                    task.Uses,
                    task.Status.ToString())).ToList(),
                stage.Checks.Select(check => new CheckStatusSnapshot(
                    check.Name,
                    check.Title,
                    check.Uses,
                    check.Status.ToString(),
                    check.Message)).ToList(),
                stage.Approval is not null
                    ? new ApprovalStatusSnapshot(stage.Approval.Status, stage.Approval.Output?.ToString(), stage.Approval.RequestedAt, stage.Approval.RespondedAt)
                    : null,
                stage.Failure is not null
                    ? new FailureStatusSnapshot(
                        stage.Failure.Reason.ToString(),
                        stage.Failure.Stage,
                        stage.Failure.TaskId,
                        stage.Failure.CheckName,
                        stage.Failure.Message)
                    : null)).ToList();

        var pending = lease is not null
            ? new PendingWorkSnapshot(lease.WorkId, lease.WorkType, lease.Stage, null, null)
            : null;

        var currentStageIndex = Math.Clamp(run.CurrentStageIndex, 0, run.Stages.Count - 1);
        var currentStage = run.Stages.Count == 0 ? null : run.Stages[currentStageIndex];
        var failure = currentStage?.Failure is not null
            ? new FailureStatusSnapshot(
                currentStage.Failure.Reason.ToString(),
                currentStage.Failure.Stage,
                currentStage.Failure.TaskId,
                currentStage.Failure.CheckName,
                currentStage.Failure.Message)
            : null;

        return new WorkflowStatusSnapshot(
            run.Id,
            WorkflowStatus(run, currentStage),
            currentStage?.Stage,
            stages,
            pending,
            failure,
            []);
    }

    private static string WorkflowStatus(WorkflowRunSnapshot run, StageRunSnapshot? currentStage)
    {
        if (!run.Started) return WorkflowRunStatus.Pending.ToString();
        if (currentStage is null) return WorkflowRunStatus.Running.ToString();
        if (currentStage.Failure is not null) return WorkflowRunStatus.Failed.ToString();
        if (run.Paused) return WorkflowRunStatus.Paused.ToString();
        if (StageStatus(currentStage) == StageRunStatus.AwaitingApproval.ToString()) return WorkflowRunStatus.AwaitingApproval.ToString();
        if (StageStatus(currentStage) == StageRunStatus.Completed.ToString() && currentStage.Order == run.Stages.Max(s => s.Order)) return WorkflowRunStatus.Completed.ToString();
        return WorkflowRunStatus.Running.ToString();
    }

    private static string StageStatus(StageRunSnapshot stage)
    {
        if (stage.Failure is not null) return StageRunStatus.Failed.ToString();
        if (!stage.Started) return StageRunStatus.Pending.ToString();
        if (stage.Approval?.Status == "awaiting") return StageRunStatus.AwaitingApproval.ToString();
        if (StageIsComplete(stage))
        {
            if (stage.RequiresApproval && stage.Approval?.Status != "approved") return StageRunStatus.Running.ToString();
            return StageRunStatus.Completed.ToString();
        }
        return StageRunStatus.Running.ToString();
    }

    private static bool StageIsComplete(StageRunSnapshot stage) =>
        stage.Initialized &&
        stage.Tasks.All(t => t.Status == TaskRunStatus.Completed) &&
        stage.Checks.All(c => c.Status == CheckRunStatus.Passed);
}