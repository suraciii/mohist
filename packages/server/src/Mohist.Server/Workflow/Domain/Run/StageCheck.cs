using System.Text.Json;

namespace Mohist.Server.Workflow.Domain.Run;

public record StageCheck(
    string Name,
    string Title,
    string? Uses,
    Dictionary<string, JsonElement?>? WithInput,
    CheckRunPhase Phase,
    int RetryCount,
    string? Message,
    JsonElement? Output);
