using System.Text.Json;

namespace Mohist.Runner.Actions;

public interface IAction
{
    Task<ActionResult> ExecuteAsync(ActionContext context);
}

public record ActionContext(
    string WorkflowRunId,
    string WorkId,
    string WorkType,
    string? Stage,
    string? Title,
    string? Uses,
    Dictionary<string, JsonElement?>? With,
    string WorkDir,
    CancellationToken CancellationToken);

public record ActionResult(
    string Status,
    string? Message = null,
    string? Output = null,
    int? ExitCode = null);
