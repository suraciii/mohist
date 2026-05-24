using System.Text.Json;
using Mohist.Runner.Transport;

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
    Dictionary<string, JsonElement?>? Variables,
    string WorkDir,
    CancellationToken CancellationToken,
    AgentSessionContext? Session = null);

public record ActionResult(
    string Status,
    string? Message = null,
    string? Output = null,
    int? ExitCode = null);
