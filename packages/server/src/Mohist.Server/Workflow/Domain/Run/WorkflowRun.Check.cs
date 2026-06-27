using System.Text.Json;
using Mohist.Server.Infrastructure;
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
            check.Status = StageCheckStatus.Passed;
            check.FinishedAt = DateTimeOffset.UtcNow;
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
            check.Status = StageCheckStatus.Failed;
            check.FinishedAt = DateTimeOffset.UtcNow;
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
            check.Status = StageCheckStatus.Pending;
            check.StartedAt = null;
            check.FinishedAt = null;
            check.Message = result.Message;
            check.Output = result.Output;
            return [new CheckPending(current.Id, check.Name, result.Message)];
        }

        public IReadOnlyList<TaskDefinition> BuildRepairTasks(
            string checkName,
            CheckFailureRepair repair,
            CheckResult? result = null)
        {
            return [BuildRepairTask(run, checkName, repair.Task, result)];
        }
    }

    private static TaskDefinition BuildRepairTask(
        WorkflowRun run, string checkName, TaskDefinition repairTask, CheckResult? result)
    {
        JsonElement? resultJson = result is null
            ? null
            : JSON.DeserializeElement(JSON.Serialize(result));
        var repairWith = repairTask.With is not null
            ? new Dictionary<string, JsonElement?>(repairTask.With)
            : new Dictionary<string, JsonElement?>();
        if (resultJson is not null && !string.Equals(checkName, "review-passed", StringComparison.Ordinal))
            repairWith["failedCheckResult"] = resultJson;

        return new TaskDefinition(
            $"{repairTask.Id}:{run.GetRepairCount(checkName) + 1}",
            repairTask.Title,
            repairTask.Uses,
            repairWith);
    }
}
