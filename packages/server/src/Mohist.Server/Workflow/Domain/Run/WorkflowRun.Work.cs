using System.Text.Json;
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
                return WorkflowWork.StageInit(current.StageId);

            var pendingTask = current.FirstPendingTask();
            if (pendingTask is not null)
                return WorkflowWork.Task(current.StageId, pendingTask.Id, pendingTask.Title, pendingTask.Uses, pendingTask.WithInput);

            var pendingChecks = current.Checks
                .Where(c => c.Phase == CheckRunPhase.Pending)
                .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
                .ToList();

            if (pendingChecks.Any())
                return WorkflowWork.Checks(current.StageId, pendingChecks);

            return null;
        }

        public bool HasIncompleteTaskUsing(string uses)
        {
            var current = run.CurrentStage();
            return current.Tasks.Any(t => t.Uses == uses && t.Phase != TaskRunPhase.Completed);
        }

        public bool HasIncompleteTaskId(string id)
        {
            var current = run.CurrentStage();
            return current.Tasks.Any(t => t.Id == id && t.Phase != TaskRunPhase.Completed);
        }

        public int RetryCountForCheck(string checkName)
        {
            var current = run.CurrentStage();
            var check = current.FindCheck(checkName);
            return check.RetryCount;
        }

        public void PatchMetadata(WorkflowRunMetadata patch)
        {
            var metadata = run.Metadata;
            run.Metadata = new WorkflowRunMetadata(
                patch.Name ?? metadata.Name,
                patch.CreatedAt,
                MergeDic(metadata.Labels, patch.Labels),
                MergeDic(metadata.Annotations, patch.Annotations));
        }

        private static Dictionary<string, string>? MergeDic(
            Dictionary<string, string>? a,
            Dictionary<string, string>? b)
        {
            if (a is null && b is null) return null;
            if (a is null) return b;
            if (b is null) return a;

            var result = new Dictionary<string, string>(a);
            foreach (var kvp in b)
                result[kvp.Key] = kvp.Value;
            return result;
        }
    }
}
