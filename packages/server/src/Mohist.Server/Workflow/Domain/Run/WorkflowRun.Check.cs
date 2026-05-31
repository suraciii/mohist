using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public void ProcessCheckResults(List<CheckResultAction> actions)
        {
            foreach (var a in actions)
            {
                switch (a.Action)
                {
                    case "pass":
                        run.PassCheck(a.Result);
                        break;
                    case "pending":
                        run.ResetCheck(a.Result);
                        break;
                    case "retry":
                        run.RepairFailedCheck(a.Result, a.RetryTask!);
                        break;
                    case "fail":
                        run.FailCheck(a.Result);
                        return;
                }
            }
        }

        public void RepairFailedCheck(CheckResult result, TaskDefinition repairTask)
        {
            run.AddRetryTask(result.Name, repairTask);
            run.ResetCheck(result);
            run.ResetStageFailure();
        }

        public void PassCheck(CheckResult result)
        {
            var current = run.CurrentStage();
            var check = current.FindCheck(result.Name);
            check.Status = StageCheckStatus.Passed;
            check.Message = result.Message;
            check.Output = result.Output;
            run.Advance();
        }

        public void FailCheck(CheckResult result)
        {
            var current = run.CurrentStage();
            var check = current.FindCheck(result.Name);
            check.Status = StageCheckStatus.Failed;
            check.Message = result.Message;
            check.Output = result.Output;
            if (current.Failure is null)
            {
                current.Failure = new FailureDetails(
                    FailureReason.CheckUnrepaired, current.Id,
                    CheckName: check.Name, Message: result.Message);
                run.Failure = current.Failure;
            }
            current.Status = StageRunStatus.Failed;
            run.Status = WorkflowRunStatus.Failed;
        }

        public void ResetCheck(CheckResult result)
        {
            var current = run.CurrentStage();
            var check = current.FindCheck(result.Name);
            check.Status = StageCheckStatus.Pending;
            check.Message = result.Message;
            check.Output = result.Output;
        }

    }
}
