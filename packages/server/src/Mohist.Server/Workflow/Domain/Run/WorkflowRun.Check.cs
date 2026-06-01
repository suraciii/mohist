using System.Text.Json;
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
                        run.ScheduleCheckRepair(a.Result.Name, a.RetryTask!, a.Result.Message, a.Result.Output);
                        break;
                    case "fail":
                        run.FailCheck(a.Result);
                        return;
                }
            }
        }

        public void RepairFailedCheck(CheckResult result, TaskDefinition repairTask)
            => run.ScheduleCheckRepair(result.Name, repairTask, result.Message, result.Output);

        public void ScheduleCheckRepair(
            string checkName,
            TaskDefinition repairTask,
            string? message = null,
            JsonElement? output = null)
        {
            var current = run.CurrentStage();
            current.ScheduleCheckRepair(checkName, repairTask, message, output);
            run.Failure = null;
            run.Status = WorkflowRunStatus.Running;
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
