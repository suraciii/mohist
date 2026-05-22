using System.Text.Json;

namespace Mohist.Server.Workflow.Grains;

public interface IWorkGrain : IGrainWithStringKey
{
    Task<WorkResult> ExecuteTaskAsync(TaskWorkItem work);
    Task<WorkResult> ExecuteCheckAsync(CheckWorkItem work);
    Task<TaskLoadWorkResult> LoadTasksAsync(TaskLoadWorkItem work);
}

public sealed record TaskWorkItem(
    string TaskId,
    string Title,
    string? Uses,
    Dictionary<string, JsonElement?>? With);

public sealed record CheckWorkItem(
    string CheckName,
    string Title,
    string? Uses,
    Dictionary<string, JsonElement?>? With);

public sealed record TaskLoadWorkItem(
    string Stage,
    string Uses,
    Dictionary<string, JsonElement?>? With);

[GenerateSerializer]
public abstract record WorkResult
{
    [GenerateSerializer]
    public sealed record TaskCompleted() : WorkResult;

    [GenerateSerializer]
    public sealed record TaskFailed(string? Reason = null) : WorkResult;

    [GenerateSerializer]
    public sealed record CheckPassed(string? Message = null, JsonElement? Output = null) : WorkResult;

    [GenerateSerializer]
    public sealed record CheckPending(string? Message = null, JsonElement? Output = null) : WorkResult;

    [GenerateSerializer]
    public sealed record CheckFailed(string CheckName, string? Message = null, JsonElement? Output = null) : WorkResult;
}

[GenerateSerializer]
public abstract record TaskLoadWorkResult
{
    [GenerateSerializer]
    public sealed record Loaded(List<LoadedTaskSnapshot> Tasks) : TaskLoadWorkResult;

    [GenerateSerializer]
    public sealed record Empty() : TaskLoadWorkResult;

    [GenerateSerializer]
    public sealed record Failed(string Message) : TaskLoadWorkResult;
}

[GenerateSerializer]
public sealed record LoadedTaskSnapshot(
    string Id,
    string Title,
    string? Uses = null,
    Dictionary<string, JsonElement?>? With = null);
