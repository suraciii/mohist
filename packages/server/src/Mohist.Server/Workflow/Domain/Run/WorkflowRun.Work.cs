using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public sealed record WorkflowWork
{
    public string Stage { get; init; }
    public string WorkType { get; init; }
    public object Data { get; init; }

    private WorkflowWork(string stage, string workType, object data)
    {
        Stage = stage;
        WorkType = workType;
        Data = data;
    }

    public static WorkflowWork StageInit(string stage) => new(stage, "stage-init", new StageInitData());
    public static WorkflowWork Task(string stage, string id, string title, string? uses, Dictionary<string, JsonElement?>? with) => new(stage, "task", new TaskData(id, title, uses, with));
    public static WorkflowWork Checks(string stage, List<CheckItem> items) => new(stage, "checks", new ChecksData(items));

    public sealed record StageInitData;
    public sealed record TaskData(string Id, string Title, string? Uses, Dictionary<string, JsonElement?>? With);
    public sealed record ChecksData(List<CheckItem> Items);
}

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public WorkflowWork? NextWork()
        {
            var current = run.CurrentStage();
            if (!current.Initialized)
                return WorkflowWork.StageInit(current.Id);

            var pendingTask = current.CurrentTask();
            if (pendingTask is not null)
                return WorkflowWork.Task(current.Id, pendingTask.Id, pendingTask.Title, pendingTask.Uses, pendingTask.WithInput);

            var pendingChecks = current.Checks
                .Where(c => c.Status == StageCheckStatus.Pending)
                .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
                .ToList();

            if (pendingChecks.Any())
                return WorkflowWork.Checks(current.Id, pendingChecks);

            return null;
        }

        public void AddRuntimeTask(
            TaskDefinition task,
            string? stage = null,
            bool invalidateChecks = false)
        {
            var current = run.CurrentStage();
            if (!current.Initialized)
                throw new WorkflowDomainException($"Cannot add runtime task: stage {current.Id} is not initialized");
            if (!string.IsNullOrWhiteSpace(stage) && stage != current.Id)
                throw new WorkflowDomainException("Cannot add runtime task to stage " + stage + "; current stage is " + current.Id);

            var newTask = TaskRun.MakeTask(current.Tasks, task);
            current.Tasks.Add(newTask);

            if (invalidateChecks)
            {
                foreach (var c in current.Checks)
                {
                    c.Status = StageCheckStatus.Pending;
                    c.Message = null;
                    c.Output = null;
                }
            }

            current.Failure = null;
            if (current.IsAwaitingApproval)
                current.ApprovalStatus = null;
            if (current.Initialized)
                current.Status = StageRunStatus.Running;

            run.Status = WorkflowRunStatus.Running;
        }

        public void InsertRuntimeTasksAfter(
            IReadOnlyList<TaskDefinition> tasks,
            bool invalidateChecks = false)
        {
            var current = run.CurrentStage();
            var afterTask = current.CurrentTask();
            var insertIndex = afterTask is not null
                ? current.Tasks.IndexOf(afterTask) + 1
                : current.Tasks.Count;

            foreach (var task in tasks)
            {
                var newTask = TaskRun.MakeTask(current.Tasks, task);
                current.Tasks.Insert(insertIndex, newTask);
                insertIndex++;
            }

            if (invalidateChecks)
            {
                foreach (var c in current.Checks)
                {
                    c.Status = StageCheckStatus.Pending;
                    c.Message = null;
                    c.Output = null;
                }
            }
        }

        private void AddRetryTask(string checkName, TaskDefinition task)
        {
            var current = run.CurrentStage();
            var newTask = TaskRun.MakeTask(current.Tasks, task);
            current.Tasks.Add(newTask);
            var check = current.FindCheck(checkName);
            check.RetryCount++;
        }

        public bool HasIncompleteTaskWithUses(string uses)
        {
            var current = run.CurrentStage();
            return current.Tasks.Any(t => t.Uses == uses && t.Status != TaskRunStatus.Completed);
        }

        public bool HasIncompleteTaskById(string id)
        {
            var current = run.CurrentStage();
            return current.Tasks.Any(t => t.Id == id && t.Status != TaskRunStatus.Completed);
        }

        public int GetRetryCount(string checkName)
        {
            var current = run.CurrentStage();
            var check = current.FindCheck(checkName);
            return check.RetryCount;
        }
    }
}
