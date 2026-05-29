namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public void PassCheck(CheckResult result)
        {
            var current = run.CurrentStage();
            var check = current.FindCheck(result.Name);
            check.Status = StageCheckStatus.Passed;
            check.Message = result.Message;
            check.Output = result.Output;
            current.TryRequestApproval();
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
                    FailureReason.CheckUnrepaired, current.StageId,
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

        public void PendingCheck(CheckResult result) => run.ResetCheck(result);

        public void InjectRetryTask(string checkName, LoadedTaskInput task)
        {
            var current = run.CurrentStage();
            var newTask = TaskRun.MakeTask(current.Tasks, task);
            current.Tasks.Add(newTask);
            var check = current.FindCheck(checkName);
            check.RetryCount++;
        }

        public void ClearStageFailure()
        {
            var current = run.CurrentStage();
            current.Failure = null;
        }
    }
}
