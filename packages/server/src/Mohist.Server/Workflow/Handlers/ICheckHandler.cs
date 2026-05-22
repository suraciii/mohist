using System.Text.Json;

namespace Mohist.Server.Workflow.Handlers;

public interface ICheckHandler
{
    Task<CheckHandlerResult> RunAsync(CheckHandlerInput input);
}

public sealed record CheckHandlerInput(
    string Name,
    string Title,
    Dictionary<string, JsonElement?>? With = null);

public sealed record CheckHandlerResult(
    string Name,
    string Status,
    string? Message = null,
    JsonElement? Output = null);
