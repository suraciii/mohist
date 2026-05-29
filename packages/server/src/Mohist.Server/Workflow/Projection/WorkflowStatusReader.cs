using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Workflow.Projection;

public static class WorkflowStatusReader
{
    public static WorkflowStatusSnapshot? Read(WorkflowRun run, WorkLease? lease)
    {
        var stages = run.Stages.Select((s, i) =>
            new StageStatusSnapshot(
                s.StageId,
                StageStatus(s),
                i,
                s.Tasks.Select(t => new TaskStatusSnapshot(
                    t.Id,
                    t.Title,
                    t.Uses,
                    t.Status.ToString())).ToList(),
                s.Checks.Select(c => new CheckStatusSnapshot(
                    c.Name,
                    c.Title,
                    c.Uses,
                    c.Status.ToString(),
                    c.Message)).ToList(),
                s.Approval is not null
                    ? new ApprovalStatusSnapshot(s.Approval.Status, s.Approval.Output?.ToString(), s.Approval.RequestedAt, s.Approval.RespondedAt)
                    : null,
                s.Failure is not null
                    ? new FailureStatusSnapshot(
                        s.Failure.Reason.ToString(),
                        s.Failure.Stage,
                        s.Failure.TaskId,
                        s.Failure.CheckName,
                        s.Failure.Message)
                    : null)).ToList();

        var pending = lease is not null
            ? new PendingWorkSnapshot(lease.WorkId, lease.WorkType, lease.Stage, null, null)
            : null;

        var currentStage = run.Stages.FirstOrDefault(s => s.StageId == run.CurrentStageId);
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
            run.Status.ToString(),
            currentStage?.StageId,
            stages,
            pending,
            failure,
            [],
            MetadataSnapshot.From(run.Metadata));
    }

    private static string StageStatus(StageRun stage)
    {
        if (stage.Failure is not null) return StageRunStatus.Failed.ToString();
        if (!stage.Initialized) return StageRunStatus.Pending.ToString();
        if (stage.Approval?.Status == "awaiting") return StageRunStatus.AwaitingApproval.ToString();
        if (StageIsComplete(stage))
        {
            if (stage.RequiresApproval && stage.Approval?.Status != "approved") return StageRunStatus.Running.ToString();
            return StageRunStatus.Completed.ToString();
        }
        return StageRunStatus.Running.ToString();
    }

    private static bool StageIsComplete(StageRun stage) =>
        stage.Initialized &&
        stage.Tasks.All(t => t.Status == TaskRunStatus.Completed) &&
        stage.Checks.All(c => c.Status == StageCheckStatus.Passed);
}