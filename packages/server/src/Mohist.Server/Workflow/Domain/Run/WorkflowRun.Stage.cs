using Mohist.Server.Workflow.Domain.Definition;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public void InitStage(
            IReadOnlyList<LoadedTaskInput> tasks,
            List<CheckDefinition> checks)
        {
            var current = run.CurrentStage();
            if (current.Initialized) return;

            var pendingRuntimeTasks = current.Tasks
                .Where(t => t.Status == TaskRunStatus.Pending)
                .Select(t => new LoadedTaskInput(t.DefinitionId, t.Title, t.Uses, t.WithInput))
                .ToList();

            var newTasks = new List<TaskRun>();
            foreach (var t in tasks)
                newTasks.Add(TaskRun.MakeTask(newTasks, t));
            foreach (var t in pendingRuntimeTasks)
                newTasks.Add(TaskRun.MakeTask(newTasks, t));

            current.Tasks = newTasks;
            current.Checks = checks
                .Select(c => new StageCheck
                {
                    Name = c.Name,
                    Title = c.Title,
                    Uses = c.Uses,
                    WithInput = c.With,
                    Status = StageCheckStatus.Pending
                })
                .ToList();
            current.Initialized = true;
            current.Status = StageRunStatus.Running;
            current.TryRequestApproval();
            run.Advance();
        }

        public void Advance()
        {
            while (true)
            {
                var current = run.CurrentStage();
                if (current.Status != StageRunStatus.Completed) break;

                var nextStage = run.Stages
                    .Where(s => s.Order > current.Order)
                    .MinBy(s => s.Order);

                if (nextStage is null)
                {
                    run.Status = WorkflowRunStatus.Completed;
                    run.CompletedAt = DateTimeOffset.UtcNow;
                    return;
                }

                nextStage.Status = StageRunStatus.Running;
                run.CurrentStageId = nextStage.StageId;
            }

            if (run.Status is not WorkflowRunStatus.Pending and not WorkflowRunStatus.Paused)
            {
                var current = run.CurrentStage();
                if (current.Status == StageRunStatus.Failed)
                    run.Status = WorkflowRunStatus.Failed;
                else if (current.Status == StageRunStatus.AwaitingApproval)
                    run.Status = WorkflowRunStatus.AwaitingApproval;
                else if (current.Status == StageRunStatus.Completed && run.Stages.Count > 0 && run.Stages[^1].StageId == current.StageId)
                {
                    run.Status = WorkflowRunStatus.Completed;
                    run.CompletedAt = DateTimeOffset.UtcNow;
                }
                else
                    run.Status = WorkflowRunStatus.Running;
            }
        }
    }
}
