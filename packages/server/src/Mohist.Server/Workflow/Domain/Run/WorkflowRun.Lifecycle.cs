using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun)
    {
        public static WorkflowRun Create(
            string id,
            WorkflowDefinition definition,
            WorkflowRunMetadata? metadata = null)
        {
            if (definition.Stages.Count == 0)
                throw new WorkflowDomainException("WorkflowDefinition requires at least one stage");

            var stages = definition.Stages
                .Select((def, i) => new StageRun
                {
                    Id = def.Stage,
                    Attempt = 1,
                    RequiresApproval = def.RequiresApproval,
                    Status = StageRunStatus.Pending
                })
                .ToList();

            return new WorkflowRun
            {
                Id = id,
                Metadata = metadata ?? new WorkflowRunMetadata(null, DateTimeOffset.UtcNow),
                Status = WorkflowRunStatus.Pending,
                CurrentStageId = stages[0].Id,
                Stages = stages
            };
        }
    }

    extension(WorkflowRun run)
    {
        public StageRun CurrentStage()
        {
            if (run.CurrentStageId is null)
                throw new WorkflowDomainException("WorkflowRun has no current stage");
            return run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId)
                ?? throw new WorkflowDomainException($"Current stage {run.CurrentStageId} not found");
        }

        public IReadOnlyList<WorkflowEvent> Start()
        {
            if (run.Status != WorkflowRunStatus.Pending && run.Status != WorkflowRunStatus.Paused)
                throw new WorkflowDomainException($"WorkflowRun is {run.Status}");

            var wasPaused = run.Status == WorkflowRunStatus.Paused;
            var current = run.CurrentStage();
            if (current.Status == StageRunStatus.Pending)
                current.Status = StageRunStatus.Running;

            run.Status = WorkflowRunStatus.Running;
            run.StartedAt ??= DateTimeOffset.UtcNow;
            return wasPaused
                ? [new WorkflowRunResumed()]
                : [new WorkflowRunStarted(), new StageStarted(current.Id)];
        }

        public IReadOnlyList<WorkflowEvent> Pause()
        {
            if (run.Status != WorkflowRunStatus.Running)
                throw new WorkflowDomainException($"WorkflowRun is {run.Status}, pause requires Running");
            run.Status = WorkflowRunStatus.Paused;
            return [new WorkflowRunPaused()];
        }

        public IReadOnlyList<WorkflowEvent> Resume()
        {
            if (run.Status != WorkflowRunStatus.Paused)
                throw new WorkflowDomainException($"WorkflowRun is {run.Status}, resume requires Paused");

            var current = run.CurrentStage();
            if (current.Status == StageRunStatus.Pending)
                current.Status = StageRunStatus.Running;

            run.Status = WorkflowRunStatus.Running;
            return [new WorkflowRunResumed()];
        }

        public IReadOnlyList<WorkflowEvent> Stop()
        {
            if (run.Status is not (WorkflowRunStatus.Running or WorkflowRunStatus.Paused))
                throw new WorkflowDomainException($"WorkflowRun is {run.Status}, stop requires Running or Paused");
            run.Status = WorkflowRunStatus.Stopped;
            return [new WorkflowRunStopped()];
        }
    }
}
