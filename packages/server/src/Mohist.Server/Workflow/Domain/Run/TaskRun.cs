using System.Text.Json;

namespace Mohist.Server.Workflow.Domain.Run;

public class TaskRun
{
    public string DefinitionId { get; }
    public int Attempt { get; }
    public string Id => $"{DefinitionId}.{Attempt}";
    public string Title { get; }
    public string? Uses { get; }
    public Dictionary<string, JsonElement?>? WithInput { get; }
    public TaskRunStatus Status { get; private set; } = TaskRunStatus.Pending;

    public TaskRun(string definitionId, int attempt, string title, string? uses = null, Dictionary<string, JsonElement?>? withInput = null)
    {
        DefinitionId = definitionId;
        Attempt = attempt;
        Title = title;
        Uses = uses;
        WithInput = withInput;
    }

    private TaskRun(string definitionId, int attempt, string title, string? uses, Dictionary<string, JsonElement?>? withInput, TaskRunStatus status)
        : this(definitionId, attempt, title, uses, withInput)
    {
        Status = status;
    }

    public void Start() => Status = TaskRunStatus.Running;
    public void Complete() => Status = TaskRunStatus.Completed;
    public void Fail() => Status = TaskRunStatus.Failed;

    public TaskRunSnapshot Snapshot() => new(DefinitionId, Attempt, Title, Uses, WithInput, Status);

    public static TaskRun Restore(TaskRunSnapshot snapshot) => new(
        snapshot.DefinitionId,
        snapshot.Attempt,
        snapshot.Title,
        snapshot.Uses,
        snapshot.WithInput,
        snapshot.Status);
}
