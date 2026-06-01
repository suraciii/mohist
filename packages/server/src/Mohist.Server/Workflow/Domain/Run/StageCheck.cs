using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;

namespace Mohist.Server.Workflow.Domain.Run;

public enum StageCheckStatus { Pending, Passed, Failed }

public sealed record CheckItem(
    string Name,
    string Title,
    string? Uses = null,
    Dictionary<string, JsonElement?>? With = null);

public sealed record CheckResult(
    string Name,
    string Status,
    string? Message = null,
    JsonElement? Output = null);

public sealed record CheckResultAction(
    CheckResult Result,
    string Action,
    IReadOnlyList<TaskDefinition>? RepairTasks = null);

public sealed class StageCheck
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public string? Uses { get; init; }
    public Dictionary<string, JsonElement?>? WithInput { get; init; }
    public StageCheckStatus Status { get; set; }
    public int RepairCount { get; set; }
    public string? Message { get; set; }
    public JsonElement? Output { get; set; }
}
