using System.Text.Json;

namespace Mohist.Runner.Actions;

public interface IAction
{
    Task<ActionResult> ExecuteAsync(ActionContext context);
}

public record ActionContext(
    string RunId,
    string Stage,
    string WorkId,
    string WorkType,
    string? Uses,
    Dictionary<string, JsonElement?>? With,
    string WorkDir);

public record ActionResult(
    string Status,
    string? Message = null,
    JsonElement? Output = null,
    int? ExitCode = null);
