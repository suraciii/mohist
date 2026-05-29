using System.Text.Json;

namespace Mohist.Server.Workflow.Domain.Run;

public enum CheckRunPhase { Pending, Passed, Failed }

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

public sealed class StageCheck
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public string? Uses { get; init; }
    public Dictionary<string, JsonElement?>? WithInput { get; init; }
    public CheckRunPhase Phase { get; set; }
    public int RetryCount { get; set; }
    public string? Message { get; set; }
    public JsonElement? Output { get; set; }
}
