using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain;

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

    public static WorkflowWork Task(string stage, string id, string title, string? uses, Dictionary<string, JsonElement?>? with, TaskArtifactCapture? artifacts = null, Dictionary<string, string>? setVars = null) => new(stage, "task", new TaskData(id, title, uses, with, artifacts, setVars));
    public static WorkflowWork Checks(string stage, List<CheckItem> items) => new(stage, "checks", new ChecksData(items));

    public sealed record TaskData(string Id, string Title, string? Uses, Dictionary<string, JsonElement?>? With, TaskArtifactCapture? Artifacts = null, Dictionary<string, string>? SetVars = null);
    public sealed record ChecksData(List<CheckItem> Items);
}

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public WorkflowWork? NextWork()
        {
            var current = run.CurrentStage();

            var pendingTask = current.CurrentTask();
            if (pendingTask is not null)
                return WorkflowWork.Task(current.Id, pendingTask.Id, pendingTask.Title, pendingTask.Uses, pendingTask.WithInput, pendingTask.Artifacts, pendingTask.SetVars);

            var pendingChecks = current.Checks
                .Where(c => c.Status == StageCheckStatus.Pending)
                .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
                .ToList();

            if (pendingChecks.Any())
                return WorkflowWork.Checks(current.Id, pendingChecks);

            return null;
        }

        public IReadOnlyList<WorkflowEvent> AddRuntimeTask(
            TaskDefinition task,
            string? stage = null,
            bool invalidateChecks = false,
            string? causedByFeedbackId = null)
            => run.AddRuntimeTasks([task], stage, invalidateChecks, causedByFeedbackId);

        public IReadOnlyList<WorkflowEvent> AddRuntimeTasks(
            IReadOnlyList<TaskDefinition> tasks,
            string? stage = null,
            bool invalidateChecks = false,
            string? causedByFeedbackId = null)
        {
            var current = run.CurrentStage();
            if (!current.Initialized)
                throw new InvalidOperationException($"Cannot add runtime task: stage {current.Id} is not initialized");
            if (!string.IsNullOrWhiteSpace(stage) && stage != current.Id)
                throw new InvalidOperationException("Cannot add runtime task to stage " + stage + "; current stage is " + current.Id);

            var runningIndex = current.Tasks.FindIndex(t => t.Status == TaskRunStatus.Running);
            var firstIncompleteIndex = current.Tasks.FindIndex(t => t.Status is not (TaskRunStatus.Completed or TaskRunStatus.Failed));
            var insertIndex = runningIndex >= 0
                ? runningIndex + 1
                : firstIncompleteIndex >= 0
                    ? firstIncompleteIndex
                    : current.Tasks.Count;

            foreach (var task in tasks)
            {
                var newTask = TaskRun.MakeTask(current.Tasks, task, causedByFeedbackId);
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

            current.Failure = null;
            if (current.IsAwaitingApproval)
                current.ApprovalStatus = null;
            if (current.Initialized)
                current.Status = StageRunStatus.Running;

            run.Status = WorkflowRunStatus.Running;
            return tasks.Count > 0 ? [new WorkflowRunResumed()] : [];
        }

        private void AddRepairTask(string checkName, TaskDefinition task)
        {
            var current = run.CurrentStage();
            var newTask = TaskRun.MakeTask(current.Tasks, task);
            current.Tasks.Add(newTask);
            var check = current.FindCheck(checkName);
            check.RepairCount++;
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

        public int GetRepairCount(string checkName)
        {
            var current = run.CurrentStage();
            var check = current.FindCheck(checkName);
            return check.RepairCount;
        }
    }
}
