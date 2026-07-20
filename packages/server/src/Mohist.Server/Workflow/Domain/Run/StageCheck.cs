using System.Text.Json;

namespace Mohist.Server.Workflow.Domain.Run;

public enum StageCheckStatus { Pending, Running, Passed, Failed }

public enum CheckResultStatus { Passed, Failed, Pending }

[GenerateSerializer]
public sealed record CheckItem(
    string Name,
    string Title,
    string? Uses = null,
    Dictionary<string, JsonElement?>? With = null);

[GenerateSerializer]
public sealed record CheckResult(
    string Name,
    CheckResultStatus Status,
    string? Message = null,
    JsonElement? Output = null,
    ExecutionError? Error = null);

public sealed class StageCheck
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public string? Uses { get; init; }
    public Dictionary<string, JsonElement?>? WithInput { get; init; }
    public StageCheckStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Message { get; set; }
    public JsonElement? Output { get; set; }
    public ExecutionError? Error { get; set; }
}
