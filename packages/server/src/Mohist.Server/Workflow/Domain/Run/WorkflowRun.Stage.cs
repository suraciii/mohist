using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public void InitStage(
            IReadOnlyList<TaskDefinition> tasks,
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
                run.CurrentStageId = current.Id;
            }

            run.Status = current.Status switch
            {
                StageRunStatus.Failed => WorkflowRunStatus.Failed,
                StageRunStatus.AwaitingApproval => WorkflowRunStatus.AwaitingApproval,
                _ => WorkflowRunStatus.Running
            };
        }
    }

    extension(StageRun stage)
    {
        internal bool IsAwaitingApproval => stage.ApprovalStatus is { Result: null };

        internal TaskRun? FirstPendingTask()
            => stage.Tasks.FirstOrDefault(t => t.Status is not (TaskRunStatus.Completed or TaskRunStatus.Failed));

        private StageCheck FindCheck(string name)
            => stage.Checks.FirstOrDefault(c => c.Name == name)
                ?? throw new WorkflowDomainException($"Check {name} not found in stage {stage.Id}");

        private StageCheck FirstPendingCheck()
            => stage.Checks.FirstOrDefault(c => c.Status == StageCheckStatus.Pending)!;

        private bool IsComplete()
        {
            if (!stage.Initialized) return false;
            var hasPendingTask = stage.Tasks.Any(t => t.Status is not (TaskRunStatus.Completed or TaskRunStatus.Failed));
            if (hasPendingTask) return false;
            return stage.Checks.All(c => c.Status == StageCheckStatus.Passed);
        }

        private void TryRequestApproval()
        {
            if (stage.RequiresApproval && stage.ApprovalStatus is null && stage.IsComplete())
            {
                stage.ApprovalStatus = new ApprovalStatus(null, DateTimeOffset.UtcNow.ToString("O"), null);
                stage.Status = StageRunStatus.AwaitingApproval;
                return;
            }
            if (stage.Failure is not null)
            {
                stage.Status = StageRunStatus.Failed;
                return;
            }
            if (stage.IsAwaitingApproval)
            {
                stage.Status = StageRunStatus.AwaitingApproval;
                return;
            }
            if (stage.IsComplete())
            {
                if (stage.RequiresApproval && stage.ApprovalStatus is not { Result: "approved" })
                {
                    stage.Status = StageRunStatus.Running;
                    return;
                }
                stage.Status = StageRunStatus.Completed;
                return;
            }
            stage.Status = StageRunStatus.Running;
        }
    }
}
