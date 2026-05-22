using System.Text.Json;

namespace Mohist.Server.Workflow.Domain.Run;

public class TaskRun
{
    public string Id { get; }
    public string Title { get; }
    public string? Uses { get; }
    public Dictionary<string, JsonElement?>? WithInput { get; }
    public TaskRunStatus Status { get; private set; } = TaskRunStatus.Pending;

    public TaskRun(string id, string title, string? uses = null, Dictionary<string, JsonElement?>? withInput = null)
    {
        Id = id;
        Title = title;
        Uses = uses;
        WithInput = withInput;
    }

    public void Reset() => Status = TaskRunStatus.Pending;
    public void Start() => Status = TaskRunStatus.Running;
    public void Complete() => Status = TaskRunStatus.Completed;
    public void Fail() => Status = TaskRunStatus.Failed;
}
