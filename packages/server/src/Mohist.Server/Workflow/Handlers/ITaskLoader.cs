using System.Text.Json;

namespace Mohist.Server.Workflow.Handlers;

public interface ITaskLoader
{
    Task<TaskLoadResult> LoadAsync(TaskLoadInput input);
}

public sealed record TaskLoadInput(
    string Stage,
    string Uses,
    Dictionary<string, JsonElement?>? With = null);

public abstract record TaskLoadResult
{
    public sealed record Loaded(List<LoadedTaskItem> Tasks) : TaskLoadResult;
    public sealed record Empty() : TaskLoadResult;
    public sealed record Missing(string? Message = null) : TaskLoadResult;
    public sealed record Invalid(string? Message = null) : TaskLoadResult;
}

public sealed record LoadedTaskItem(
    string Id,
    string Title,
    string? Uses = null,
    Dictionary<string, JsonElement?>? With = null);
