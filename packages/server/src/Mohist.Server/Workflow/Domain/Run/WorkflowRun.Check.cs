using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
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
                    case "repair":
                        events.AddRange(run.ScheduleCheckRepair(a.Result.Name, a.RepairTasks!, a.Result.Message, a.Result.Output));
                        break;
                    case "fail":
                        events.AddRange(run.FailCheck(a.Result));
                        return events;
                }
            }
            return events;
        }

        public IReadOnlyList<WorkflowEvent> RepairFailedCheck(CheckResult result, TaskDefinition repairTask)
            => run.ScheduleCheckRepair(result.Name, [repairTask], result.Message, result.Output);

        public IReadOnlyList<WorkflowEvent> ScheduleCheckRepair(
            string checkName,
            IReadOnlyList<TaskDefinition> repairTasks,
            string? message = null,
            JsonElement? output = null)
        {
            var current = run.CurrentStage();
            current.ScheduleCheckRepair(checkName, repairTasks, message, output);
            run.Failure = null;
            run.Status = WorkflowRunStatus.Running;
            var taskIds = current.Tasks
                .TakeLast(repairTasks.Count)
                .Select(t => t.Id)
                .ToArray();
            return [
                new RepairScheduled(current.Id, checkName, taskIds),
                new WorkflowRunResumed()
            ];
        }

        public IReadOnlyList<WorkflowEvent> PassCheck(CheckResult result)
        {
            var current = run.CurrentStage();
            var check = current.FindCheck(result.Name);
            ClearDispatch(check);
            check.Status = StageCheckStatus.Passed;
            check.Message = result.Message;
            check.Output = result.Output;
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
            ClearDispatch(check);
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
            ClearDispatch(check);
            check.Status = StageCheckStatus.Pending;
            check.Message = result.Message;
            check.Output = result.Output;
            return [new CheckPending(current.Id, check.Name, result.Message)];
        }

    }

    private static void ClearDispatch(StageCheck check)
    {
        check.DispatchWorkId = null;
        check.DispatchRunnerId = null;
        check.DispatchedAt = null;
    }
}
