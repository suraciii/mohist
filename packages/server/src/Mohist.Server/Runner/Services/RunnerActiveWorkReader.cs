using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Runner.Services;

public interface IRunnerActiveWorkReader
{
    Task<IReadOnlyList<RunnerActiveWorkItem>> ListAsync(
        string runnerId,
        CancellationToken ct = default);
}

public sealed class RunnerActiveWorkReader(
    WorkflowRunQuerier workflowRuns,
    IAgentJobStore agentJobs) : IRunnerActiveWorkReader, IScopedService
{
    public async Task<IReadOnlyList<RunnerActiveWorkItem>> ListAsync(
        string runnerId,
        CancellationToken ct = default)
    {
        var activeWorks = new List<RunnerActiveWorkItem>();
        foreach (var workflowRunId in await workflowRuns.FindRunningAssignedToAsync(runnerId, ct))
        {
            var run = await workflowRuns.LoadAsync(workflowRunId, ct);
            if (run is null)
                continue;

            var issue = IssueFromRun(run);
            var stage = run.CurrentStage();
            var task = stage.RunningTask;
            if (task is not null)
            {
                activeWorks.Add(new RunnerActiveWorkItem(
                    WorkId: task.WorkId ?? task.Id,
                    OwnerKind: WorkDispatchOwnerKinds.Workflow,
                    OwnerId: workflowRunId,
                    WorkType: "task",
                    Stage: stage.Id,
                    Title: task.Title,
                    Issue: issue,
                    TakenAt: task.StartedAt,
                    ActionAttemptId: task.Id,
                    IsAgentWork: false));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(stage.ChecksWorkId))
            {
                activeWorks.Add(new RunnerActiveWorkItem(
                    WorkId: stage.ChecksWorkId,
                    OwnerKind: WorkDispatchOwnerKinds.Workflow,
                    OwnerId: workflowRunId,
                    WorkType: "checks",
                    Stage: stage.Id,
                    Title: "Stage checks",
                    Issue: issue,
                    TakenAt: null));
            }
        }

        foreach (var work in await agentJobs.ListRunningForRunnerAsync(runnerId, ct))
        {
            activeWorks.Add(new RunnerActiveWorkItem(
                WorkId: work.WorkId!,
                OwnerKind: WorkDispatchOwnerKinds.AgentJob,
                OwnerId: work.JobKey,
                WorkType: work.WorkType ?? "agent-job",
                Stage: work.Stage,
                Title: work.Title,
                Issue: work.IssueProjectId is not null && work.IssueNumber is not null
                    ? new WorkIssueRef(work.IssueProjectId, work.IssueNumber.Value)
                    : null,
                TakenAt: work.RunningSince));
        }

        return activeWorks;
    }

    private static WorkIssueRef? IssueFromRun(WorkflowRun run)
    {
        if (string.IsNullOrWhiteSpace(run.Metadata.ProjectId)
            || run.Metadata.IssueNumber is not > 0)
            return null;
        return new WorkIssueRef(run.Metadata.ProjectId, run.Metadata.IssueNumber.Value);
    }
}
