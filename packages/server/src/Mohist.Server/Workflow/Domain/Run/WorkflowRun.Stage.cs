using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public IReadOnlyList<WorkflowEvent> InitializeStage(
            IReadOnlyList<TaskDefinition> tasks,
            List<CheckDefinition> checks,
            DateTimeOffset now,
            bool advance = true)
        {
            var current = run.CurrentStage();
            if (current.Initialized) return [];

            var newTasks = new List<TaskRun>();
            foreach (var t in tasks)
                newTasks.Add(TaskRun.MakeTask(newTasks, t, current.Attempt));

            current.Tasks = newTasks;
            current.Checks = checks
                .Select(c => new StageCheck
                {
                    Name = c.Id,
                    Title = c.Title ?? c.Id,
                    Uses = c.Uses,
                    WithInput = c.With,
                    Status = StageCheckStatus.Pending
                })
                .ToList();
            current.Initialized = true;
            current.Status = StageRunStatus.Running;
            return advance ? run.Advance(now) : [];
        }

        private IReadOnlyList<WorkflowEvent> Advance(DateTimeOffset now)
        {
            if (run.Status == WorkflowRunStatus.Paused) return [];
            var events = new List<WorkflowEvent>();

            var current = run.CurrentStage();
            var statusBefore = current.Status;
            current.TryRequestApproval(now);
            if (statusBefore != StageRunStatus.AwaitingApproval && current.Status == StageRunStatus.AwaitingApproval)
                events.Add(new StageApprovalRequested(current.Id));

            while (current.Status == StageRunStatus.Completed)
            {
                events.Add(new StageCompleted(current.Id));
                var idx = run.Stages.IndexOf(current) + 1;
                if (idx >= run.Stages.Count)
                {
                    run.Status = WorkflowRunStatus.Completed;
                    run.CompletedAt = now;
                    events.Add(new WorkflowRunCompleted());
                    return events;
                }

                current = run.Stages[idx];
                current.Status = StageRunStatus.Running;
                run.CurrentStageId = current.Id;
                events.Add(new StageStarted(current.Id));
            }

            SetStatusAndTrackReadySince(run, current.Status switch
            {
                StageRunStatus.Failed => WorkflowRunStatus.Failed,
                StageRunStatus.AwaitingApproval => WorkflowRunStatus.AwaitingApproval,
                _ => WaitingForDispatchStatus(run)
            }, now);
            return events;
        }
    }

    extension(StageRun stage)
    {
        public TaskRun? RunningTask =>
            stage.Tasks.FirstOrDefault(t => t.Status == TaskRunStatus.Running);

        internal bool IsAwaitingApproval =>
            stage.ApprovalStatus is { Result: null } && stage.ApprovalStatus.DecidedBy is null;

        /// <summary>
        /// True when the stage is part of an active approval feedback loop
        /// (awaiting approval, has feedback requested, or has an apply-feedback
        /// task pending or running). Retryability evaluation MUST treat such
        /// stages as not retryable.
        /// </summary>
        internal bool IsFeedbackLoop =>
            stage.IsAwaitingApproval
            || (stage.Initialized
                && stage.Tasks.Any(t =>
                    t.CausedByFeedbackId is not null
                    && t.Status is not (TaskRunStatus.Completed or TaskRunStatus.Failed)));

        private TaskRun? CurrentTask()
            => stage.Tasks.FirstOrDefault(t => t.Status is not (TaskRunStatus.Completed or TaskRunStatus.Failed));

        private StageCheck FindCheck(string name)
            => stage.Checks.FirstOrDefault(c => c.Name == name)
                ?? throw new InvalidOperationException($"Check {name} not found in stage {stage.Id}");

        public bool HasNoPendingTasksAndPassedChecks()
        {
            if (!stage.Initialized) return false;
            var hasPendingTask = stage.Tasks.Any(t => t.Status is not (TaskRunStatus.Completed or TaskRunStatus.Failed));
            if (hasPendingTask) return false;
            return stage.Checks.All(c => c.Status == StageCheckStatus.Passed);
        }

        private void TryRequestApproval(DateTimeOffset now)
        {
            if (stage.RequiresApproval && stage.HasNoPendingTasksAndPassedChecks())
            {
                // The gate needs a fresh ApprovalStatus when the previous
                // decision was an unresolved reject-via-changes (Result is
                // null but DecidedBy is set, leaving feedback tasks behind).
                // Skipping that branch when the prior decision was an
                // approval guards against accidentally wiping the
                // approved decision when advance fires after a follow-up
                // task scheduled by the approval itself.
                if (stage.ApprovalStatus is null
                    || (stage.ApprovalStatus.Result is null && stage.ApprovalStatus.DecidedBy is not null))
                {
                    stage.ApprovalStatus = new ApprovalStatus(null, now.ToString("O"), null);
                    stage.Status = StageRunStatus.AwaitingApproval;
                    return;
                }
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
            if (stage.HasNoPendingTasksAndPassedChecks())
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

        private void RetryFailedTask(string taskRunId)
        {
            var failedTask = stage.Tasks.LastOrDefault(t => t.Id == taskRunId && t.Status == TaskRunStatus.Failed)
                ?? throw new InvalidOperationException($"Failed task {taskRunId} not found or not in failed state");

            var newTask = TaskRun.MakeTask(stage.Tasks, failedTask.ToDefinition(), stage.Attempt);
            var failedTaskIndex = stage.Tasks.IndexOf(failedTask);
            stage.Tasks.Insert(failedTaskIndex + 1, newTask);
            stage.Failure = null;
            stage.Status = StageRunStatus.Running;
        }

        private void RetryFailedCheck(string? checkName)
        {
            var failedCheck = stage.Checks.FirstOrDefault(c => c.Name == checkName && c.Status == StageCheckStatus.Failed)
                ?? throw new InvalidOperationException($"Failed check {checkName} not found or not in failed state");

            failedCheck.Status = StageCheckStatus.Pending;
            failedCheck.Message = null;
            failedCheck.Output = null;
            stage.Failure = null;
            stage.Status = StageRunStatus.Running;
        }
    }
}
