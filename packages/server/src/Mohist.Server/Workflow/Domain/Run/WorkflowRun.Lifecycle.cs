using System.Text.Json;
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
            DateTimeOffset now,
            WorkflowRunMetadata? metadata = null)
        {
            if (definition.Stages.Count == 0)
                throw new InvalidOperationException("WorkflowDefinition requires at least one stage");

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
                Metadata = metadata ?? new WorkflowRunMetadata(null, now),
                Status = WorkflowRunStatus.Created,
                CurrentStageId = stages[0].Id,
                Stages = stages,
            };
        }

        public static WorkflowRun Create(
            string id,
            WorkflowStructure structure,
            DateTimeOffset now,
            WorkflowRunMetadata? metadata = null)
        {
            if (structure.Stages.Count == 0)
                throw new InvalidOperationException("WorkflowStructure requires at least one stage");

            var stages = structure.Stages
                .Select(s => new StageRun
                {
                    Id = s.Stage,
                    Attempt = 1,
                    RequiresApproval = s.RequiresApproval,
                    Status = StageRunStatus.Pending
                })
                .ToList();

            return new WorkflowRun
            {
                Id = id,
                Metadata = metadata ?? new WorkflowRunMetadata(null, now),
                Status = WorkflowRunStatus.Created,
                CurrentStageId = stages[0].Id,
                Stages = stages,
            };
        }
    }

    extension(WorkflowRun run)
    {
        public StageRun CurrentStage()
        {
            if (run.CurrentStageId is null)
                throw new InvalidOperationException("WorkflowRun has no current stage");
            return run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId)
                ?? throw new InvalidOperationException($"Current stage {run.CurrentStageId} not found");
        }

        public IReadOnlyList<WorkflowEvent> Start(DateTimeOffset now, bool dispatchable = true)
        {
            if (run.Status != WorkflowRunStatus.Created && run.Status != WorkflowRunStatus.Paused)
                throw new InvalidOperationException($"WorkflowRun is {run.Status}");

            var wasPaused = run.Status == WorkflowRunStatus.Paused;
            var current = run.CurrentStage();
            if (current.Status == StageRunStatus.Pending)
                current.Status = StageRunStatus.Running;

            run.DispatchActivated = dispatchable;
            if (dispatchable)
                SetStatusAndTrackReadySince(run, wasPaused
                    ? ActiveOrWaitingForDispatchStatus(run)
                    : WorkflowRunStatus.Pending,
                    now);
            run.StartedAt ??= now;
            return wasPaused
                ? [new WorkflowRunResumed()]
                : [new WorkflowRunStarted(), new StageStarted(current.Id)];
        }

        public IReadOnlyList<WorkflowEvent> ActivateForDispatch(DateTimeOffset now)
        {
            if (run.DispatchActivated != false) return [];
            if (run.Status != WorkflowRunStatus.Created)
                throw new InvalidOperationException($"WorkflowRun is {run.Status}, activation requires Created");

            run.DispatchActivated = true;
            return run.Advance(now);
        }

        public IReadOnlyList<WorkflowEvent> Pause()
        {
            if (run.Status is not (WorkflowRunStatus.Pending or WorkflowRunStatus.Ready or WorkflowRunStatus.Running))
                throw new InvalidOperationException($"WorkflowRun is {run.Status}, pause requires an executing state");
            run.Status = WorkflowRunStatus.Paused;
            return [new WorkflowRunPaused()];
        }

        public IReadOnlyList<WorkflowEvent> Resume(DateTimeOffset now)
        {
            if (run.Status != WorkflowRunStatus.Paused)
                throw new InvalidOperationException($"WorkflowRun is {run.Status}, resume requires Paused");

            var current = run.CurrentStage();
            if (current.Status == StageRunStatus.Pending)
                current.Status = StageRunStatus.Running;

            ApplyActiveOrWaitingForDispatchStatus(run, now);
            return [new WorkflowRunResumed()];
        }

        public IReadOnlyList<WorkflowEvent> Stop()
        {
            if (run.Status is not (WorkflowRunStatus.Created or WorkflowRunStatus.Pending or WorkflowRunStatus.Ready or WorkflowRunStatus.Running or WorkflowRunStatus.AwaitingApproval or WorkflowRunStatus.Paused))
                throw new InvalidOperationException($"WorkflowRun is {run.Status}, stop requires a non-terminal started state");

            run.ClearStaleApprovalGate();
            run.Status = WorkflowRunStatus.Stopped;
            return [new WorkflowRunStopped()];
        }
    }
}
