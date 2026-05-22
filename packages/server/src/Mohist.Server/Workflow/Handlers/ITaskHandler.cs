using System.Text.Json;

namespace Mohist.Server.Workflow.Handlers;

public interface ITaskHandler
{
    Task<TaskHandlerResult> RunAsync(TaskHandlerInput input);
}

public sealed record TaskHandlerInput(
    string Id,
    string Title,
    Dictionary<string, JsonElement?>? With = null);

public sealed record TaskHandlerResult(
    string Status,
    string? Reason = null);
