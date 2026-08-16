using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public IReadOnlyList<WorkflowEvent> ProcessCheckResults(IReadOnlyList<CheckResult> results, DateTimeOffset now)
        {
            if (results.Count == 0) return [];

            var current = run.CurrentStage();
            current.Interruption = null;
            var events = new List<WorkflowEvent>();
            CheckFailed? firstFailure = null;

            foreach (var result in results)
            {
                switch (result.Status)
                {
                    case CheckResultStatus.Passed:
                        events.Add(run.ApplyPassedCheck(current, result, now));
                        break;
                    case CheckResultStatus.Pending:
                        events.Add(run.ApplyPendingCheck(current, result));
                        break;
                    case CheckResultStatus.Failed:
                        var failed = run.ApplyFailedCheck(current, result, now);
                        events.Add(failed);
                        firstFailure ??= failed;
                        break;
                }
            }

            current.ChecksWorkId = null;
            if (firstFailure is not null)
            {
                events.AddRange(run.FailCurrentStageForCheck(current, firstFailure.CheckName, firstFailure.Message));
                return events;
            }

            events.AddRange(run.Advance(now));
            return events;
        }

        public IReadOnlyList<WorkflowEvent> FailRunningChecks(string message, DateTimeOffset now)
        {
            var current = run.CurrentStage();
            if (string.IsNullOrWhiteSpace(current.ChecksWorkId)) return [];

            var results = current.Checks
                .Where(c => c.Status == StageCheckStatus.Running)
                .Select(c => new CheckResult(c.Name, CheckResultStatus.Failed, message))
                .ToList();

            return run.ProcessCheckResults(results, now);
        }

        public IReadOnlyList<WorkflowEvent> PassCheck(CheckResult result, DateTimeOffset now)
        {
            var current = run.CurrentStage();
            current.ChecksWorkId = null;
            var events = new List<WorkflowEvent>
            {
                run.ApplyPassedCheck(current, result, now)
            };
            events.AddRange(run.Advance(now));
            return events;
        }

        public IReadOnlyList<WorkflowEvent> FailCheck(CheckResult result, DateTimeOffset now)
        {
            var current = run.CurrentStage();
            current.ChecksWorkId = null;
            var failed = run.ApplyFailedCheck(current, result, now);
            var events = new List<WorkflowEvent> { failed };
            events.AddRange(run.FailCurrentStageForCheck(current, failed.CheckName, failed.Message));
            return events;
        }

        public IReadOnlyList<WorkflowEvent> ResetCheck(CheckResult result, DateTimeOffset now)
        {
            var current = run.CurrentStage();
            current.ChecksWorkId = null;
            var events = new List<WorkflowEvent>
            {
                run.ApplyPendingCheck(current, result)
            };
            events.AddRange(run.Advance(now));
            return events;
        }

        private CheckPassed ApplyPassedCheck(StageRun current, CheckResult result, DateTimeOffset now)
        {
            var check = current.FindCheck(result.Name);
            check.Status = StageCheckStatus.Passed;
            check.FinishedAt = now;
            check.Message = result.Message;
            check.Output = result.Output;
            check.Error = result.Error;
            return new CheckPassed(current.Id, check.Name, result.Message);
        }

        private CheckFailed ApplyFailedCheck(StageRun current, CheckResult result, DateTimeOffset now)
        {
            var check = current.FindCheck(result.Name);
            check.Status = StageCheckStatus.Failed;
            check.FinishedAt = now;
            check.Message = result.Message;
            check.Output = result.Output;
            check.Error = result.Error;
            return new CheckFailed(current.Id, check.Name, result.Message);
        }

        private CheckPending ApplyPendingCheck(StageRun current, CheckResult result)
        {
            var check = current.FindCheck(result.Name);
            check.Status = StageCheckStatus.Pending;
            check.StartedAt = null;
            check.FinishedAt = null;
            check.Message = result.Message;
            check.Output = result.Output;
            check.Error = result.Error;
            return new CheckPending(current.Id, check.Name, result.Message);
        }

        private IReadOnlyList<WorkflowEvent> FailCurrentStageForCheck(
            StageRun current,
            string checkName,
            string? message)
        {
            if (current.Failure is null)
            {
                current.Failure = new FailureDetails(
                    FailureReason.CheckFailed, current.Id,
                    CheckName: checkName, Message: message);
                run.Failure = current.Failure;
            }
            current.Status = StageRunStatus.Failed;
            run.Status = WorkflowRunStatus.Failed;
            return [
                new StageFailed(current.Id, message),
                new WorkflowRunFailed(message)
            ];
        }
    }
}
