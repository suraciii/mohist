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
            if (current.Initialized && current.TasksFrom is null) return;

            var pendingRuntimeTasks = current.Tasks
                .Where(t => t.Phase == TaskRunPhase.Pending)
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
                    Phase = CheckRunPhase.Pending
                })
                .ToList();
            current.Initialized = true;
            current.TasksFrom = null;
            current.Phase = StageRunPhase.Running;
            current.TryRequestApproval();
            run.Advance();
        }

        public void MarkLoadPending(string? uses)
        {
            var current = run.CurrentStage();
            current.Initialized = true;
            current.TasksFrom = uses;
        }

        public void ClearTasksFrom()
        {
            var current = run.CurrentStage();
            current.TasksFrom = null;
        }

        public void Advance()
        {
            while (true)
            {
                var current = run.CurrentStage();
                if (current.Phase != StageRunPhase.Completed) break;

                var nextStage = run.Stages
                    .Where(s => s.Order > current.Order)
                    .MinBy(s => s.Order);

                if (nextStage is null)
                {
                    run.Phase = WorkflowRunPhase.Completed;
                    run.CompletedAt = DateTimeOffset.UtcNow;
                    return;
                }

                nextStage.Phase = StageRunPhase.Running;
                run.CurrentStageId = nextStage.StageId;
            }
            run.RecomputePhase();
        }
    }
}
