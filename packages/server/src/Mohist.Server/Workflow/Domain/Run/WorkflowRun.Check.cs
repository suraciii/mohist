using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public IReadOnlyList<WorkflowEvent> ProcessCheckResults(List<CheckResultAction> actions)
        {
            var events = new List<WorkflowEvent>();
            foreach (var a in actions)
            {
                switch (a.Action)
                {
                    case "pass":
                        events.AddRange(run.PassCheck(a.Result));
                        break;
                    case "pending":
                        events.AddRange(run.ResetCheck(a.Result));
                        break;
                    case "fail":
                        events.AddRange(run.FailCheck(a.Result));
                        return events;
                }
            }
            return events;
        }

        public IReadOnlyList<WorkflowEvent> PassCheck(CheckResult result)
        {
            var current = run.CurrentStage();
            var check = current.FindCheck(result.Name);
            check.Status = StageCheckStatus.Passed;
            check.FinishedAt = DateTimeOffset.UtcNow;
            check.Message = result.Message;
            check.Output = result.Output;
            current.ChecksWorkId = null;
            var events = new List<WorkflowEvent>
            {
                new CheckPassed(current.Id, check.Name, result.Message)
            };
            events.AddRange(run.Advance());
            return events;
        }

        public IReadOnlyList<WorkflowEvent> FailCheck(CheckResult result)
        {
            var current = run.CurrentStage();
            var check = current.FindCheck(result.Name);
            check.Status = StageCheckStatus.Failed;
            check.FinishedAt = DateTimeOffset.UtcNow;
            check.Message = result.Message;
            check.Output = result.Output;
            current.ChecksWorkId = null;
            if (current.Failure is null)
            {
                current.Failure = new FailureDetails(
                    FailureReason.CheckFailed, current.Id,
                    CheckName: check.Name, Message: result.Message);
                run.Failure = current.Failure;
            }
            current.Status = StageRunStatus.Failed;
            run.Status = WorkflowRunStatus.Failed;
            return [
                new CheckFailed(current.Id, check.Name, result.Message),
                new StageFailed(current.Id, result.Message),
                new WorkflowRunFailed(result.Message)
            ];
        }

        public IReadOnlyList<WorkflowEvent> ResetCheck(CheckResult result)
        {
            var current = run.CurrentStage();
            var check = current.FindCheck(result.Name);
            check.Status = StageCheckStatus.Pending;
            check.StartedAt = null;
            check.FinishedAt = null;
            check.Message = result.Message;
            check.Output = result.Output;
            current.ChecksWorkId = null;
            var events = new List<WorkflowEvent>
            {
                new CheckPending(current.Id, check.Name, result.Message)
            };
            // Re-evaluate the run state: the check has been re-queued,
            // so the run is no longer in-flight. With the worker still
            // assigned, it lands on Ready so the next poll re-dispatches
            // the check work.
            events.AddRange(run.Advance());
            return events;
        }
    }
}
