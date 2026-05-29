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

            var newTasks = new List<TaskRun>();
            foreach (var t in tasks)
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
            run.Advance();
        }

        private void Advance()
        {
            if (run.Status is WorkflowRunStatus.Pending or WorkflowRunStatus.Paused) return;

            var current = run.CurrentStage();
            current.TryRequestApproval();
            while (current.Status == StageRunStatus.Completed)
            {
                var idx = run.Stages.IndexOf(current) + 1;
                if (idx >= run.Stages.Count)
                {
                    run.Status = WorkflowRunStatus.Completed;
                    run.CompletedAt = DateTimeOffset.UtcNow;
                    return;
                }

                current = run.Stages[idx];
                current.Status = StageRunStatus.Running;
                run.CurrentStageId = current.StageId;
            }

            run.Status = current.Status switch
            {
                StageRunStatus.Failed => WorkflowRunStatus.Failed,
                StageRunStatus.AwaitingApproval => WorkflowRunStatus.AwaitingApproval,
                _ => WorkflowRunStatus.Running
            };
        }
    }
}
