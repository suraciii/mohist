using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Views;

namespace Mohist.Server.Workflow.Projection;

public static class WorkflowStatusReader
{
    public static WorkflowStatusView? Read(WorkflowRun run, WorkLease? lease)
    {
        var stages = run.Stages.Select((s, i) =>
            new StageStatusView(
                s.Id,
                StageStatus(s),
                i,
                s.Tasks.Select(t => new TaskStatusView(
                    t.Id,
                    t.Title,
                    t.Uses,
                    t.Status.ToString())).ToList(),
                s.Checks.Select(c => new CheckStatusView(
                    c.Name,
                    c.Title,
                    c.Uses,
                    c.Status.ToString(),
                    c.Message)).ToList(),
                s.ApprovalStatus is not null
                    ? new ApprovalStatusView(s.ApprovalStatus.Result, s.ApprovalStatus.RequestedAt, s.ApprovalStatus.RespondedAt)
                    : null,
                s.Failure is not null
                    ? new FailureStatusView(
                        s.Failure.Reason.ToString(),
                        s.Failure.Stage,
                        s.Failure.TaskId,
                        s.Failure.CheckName,
                        s.Failure.Message)
                    : null)).ToList();

        var pending = lease is not null
            ? new PendingWorkView(lease.WorkId, lease.WorkType, lease.Stage, null, null)
            : null;

        var currentStage = run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId);
        var failure = currentStage?.Failure is not null
            ? new FailureStatusView(
                currentStage.Failure.Reason.ToString(),
                currentStage.Failure.Stage,
                currentStage.Failure.TaskId,
                currentStage.Failure.CheckName,
                currentStage.Failure.Message)
            : null;

        return new WorkflowStatusView(
            run.Id,
            run.Status.ToString(),
            currentStage?.Id,
            stages,
            pending,
            failure,
            [],
            run.Metadata is null ? null : new MetadataView(run.Metadata.Name, run.Metadata.Labels, run.Metadata.Annotations, run.Metadata.CreatedAt));
    }

    private static string StageStatus(StageRun stage)
    {
        if (stage.Failure is not null) return StageRunStatus.Failed.ToString();
        if (!stage.Initialized) return StageRunStatus.Pending.ToString();
        if (stage.IsAwaitingApproval) return StageRunStatus.AwaitingApproval.ToString();
        if (StageIsComplete(stage))
        {
            if (stage.RequiresApproval && stage.ApprovalStatus is not { Result: "approved" }) return StageRunStatus.Running.ToString();
            return StageRunStatus.Completed.ToString();
        }
        return StageRunStatus.Running.ToString();
    }

    private static bool StageIsComplete(StageRun stage) =>
        stage.Initialized &&
        stage.Tasks.All(t => t.Status == TaskRunStatus.Completed) &&
        stage.Checks.All(c => c.Status == StageCheckStatus.Passed);
}