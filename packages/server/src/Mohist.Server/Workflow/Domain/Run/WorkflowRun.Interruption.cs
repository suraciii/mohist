namespace Mohist.Server.Workflow.Domain.Run;

public enum WorkInterruptionUpdate
{
    Rejected,
    Unchanged,
    Updated,
}

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public WorkInterruptionUpdate RecordWorkInterruption(
            string ownerId,
            string workerId,
            string reasonCode,
            DateTimeOffset recordedAt,
            DateTimeOffset recoveryDeadlineAt)
        {
            var active = run.CurrentActiveWorkFor(workerId);
            if (active is null)
                return WorkInterruptionUpdate.Rejected;

            var interruption = new WorkInterruption(
                reasonCode,
                active.WorkId,
                ownerId,
                recordedAt,
                recoveryDeadlineAt);

            if (active.IsTask)
            {
                var task = run.CurrentStage().Tasks.SingleOrDefault(candidate =>
                    string.Equals(candidate.Id, active.TaskRunId, StringComparison.Ordinal));
                if (task is null || task.AgentResultSettlement is not null)
                    return WorkInterruptionUpdate.Rejected;
                if (task.Interruption is not null)
                    return WorkInterruptionUpdate.Unchanged;

                task.Interruption = interruption;
                return WorkInterruptionUpdate.Updated;
            }

            if (!active.IsChecks)
                return WorkInterruptionUpdate.Rejected;
            if (run.CurrentStage().Interruption is not null)
                return WorkInterruptionUpdate.Unchanged;

            run.CurrentStage().Interruption = interruption;
            return WorkInterruptionUpdate.Updated;
        }

        public bool ClearWorkInterruption(string workId, string workerId)
        {
            var active = run.FindActiveWork(workId, workerId);
            if (active is null)
                return false;

            var current = run.CurrentStage();
            if (active.IsTask && active.TaskRunId is { } taskRunId)
            {
                var task = current.Tasks.SingleOrDefault(candidate =>
                    string.Equals(candidate.Id, taskRunId, StringComparison.Ordinal));
                if (task?.Interruption is null)
                    return false;

                task.Interruption = null;
                return true;
            }

            if (!active.IsChecks || current.Interruption is null)
                return false;

            current.Interruption = null;
            return true;
        }

        public WorkInterruption? CurrentWorkInterruption()
        {
            var current = run.CurrentStage();
            return current.RunningTask?.Interruption ?? current.Interruption;
        }

        public IReadOnlyList<WorkflowEvent> FailInterruptedWorkIfDue(DateTimeOffset now)
        {
            var current = run.CurrentStage();
            var task = current.RunningTask;
            if (task?.WorkflowTaskRecovery is not null)
                return [];
            if (task?.Interruption is { } taskInterruption
                && taskInterruption.RecoveryDeadlineAt <= now)
            {
                task.Interruption = null;
                var events = run.FailTask(
                    current.Id,
                    task.Id,
                    new TaskResult("failed", taskInterruption.ReasonCode),
                    now);
                return events.Count == 0
                    ? []
                    : [
                        new TaskInterrupted(
                            current.Id,
                            task.Id,
                            taskInterruption.WorkId,
                            taskInterruption.ReasonCode,
                            taskInterruption.RecoveryDeadlineAt),
                        .. events
                    ];
            }

            var checksInterruption = current.Interruption;
            if (checksInterruption is null || checksInterruption.RecoveryDeadlineAt > now)
                return [];

            current.Interruption = null;
            var checkEvents = run.FailRunningChecks(checksInterruption.ReasonCode, now);
            if (checkEvents.Count == 0)
            {
                current.Interruption = checksInterruption;
                return [];
            }

            return [
                new ChecksInterrupted(
                    current.Id,
                    checksInterruption.WorkId,
                    checksInterruption.ReasonCode,
                    checksInterruption.RecoveryDeadlineAt),
                .. checkEvents
            ];
        }
    }
}
