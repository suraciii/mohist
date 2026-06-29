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
                Metadata = metadata ?? new WorkflowRunMetadata(null, DateTimeOffset.UtcNow),
                Status = WorkflowRunStatus.Pending,
                CurrentStageId = stages[0].Id,
                Stages = stages,
            };
        }

        /// <summary>
        /// Create overload that takes the narrow <see cref="WorkflowStructure"/>
        /// projection exposed by <c>WorkflowProfileManager.LoadStructureAsync</c>.
        /// The grain uses this so it never has to touch a full
        /// <see cref="WorkflowDefinition"/>; tasks, checks, and lock behavior
        /// are pulled in only when a stage actually initializes via
        /// <c>LoadStageSpecsAsync</c>.
        /// </summary>
        public static WorkflowRun Create(
            string id,
            WorkflowStructure structure,
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
                Metadata = metadata ?? new WorkflowRunMetadata(null, DateTimeOffset.UtcNow),
                Status = WorkflowRunStatus.Pending,
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

        private void MarkStageReached(string stageId)
        {
            if (!run.ReachedStageIds.Contains(stageId, StringComparer.Ordinal))
                run.ReachedStageIds.Add(stageId);
        }

        public IReadOnlyList<WorkflowEvent> Start()
        {
            if (run.Status != WorkflowRunStatus.Pending && run.Status != WorkflowRunStatus.Paused)
                throw new InvalidOperationException($"WorkflowRun is {run.Status}");

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
                throw new InvalidOperationException($"WorkflowRun is {run.Status}, pause requires Running");
            run.Status = WorkflowRunStatus.Paused;
            return [new WorkflowRunPaused()];
        }

        public IReadOnlyList<WorkflowEvent> Resume()
        {
            if (run.Status != WorkflowRunStatus.Paused)
                throw new InvalidOperationException($"WorkflowRun is {run.Status}, resume requires Paused");

            var current = run.CurrentStage();
            if (current.Status == StageRunStatus.Pending)
                current.Status = StageRunStatus.Running;

            run.Status = WorkflowRunStatus.Running;
            return [new WorkflowRunResumed()];
        }

        public IReadOnlyList<WorkflowEvent> Stop()
        {
            if (run.Status is not (WorkflowRunStatus.Running or WorkflowRunStatus.Paused))
                throw new InvalidOperationException($"WorkflowRun is {run.Status}, stop requires Running or Paused");
            run.Status = WorkflowRunStatus.Stopped;
            return [new WorkflowRunStopped()];
        }
    }
}
