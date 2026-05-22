using System.Text.Json;

namespace Mohist.Runner.Actions;

public record ActionDefinition(
    string Name,
    string Description,
    ActionExecutionData Execution);

public abstract record ActionExecutionData
{
    public sealed record Process(
        string Command,
        string[] Args,
        Dictionary<string, string>? Env = null) : ActionExecutionData;

    public sealed record Script(
        string Shell,
        string ScriptContent,
        Dictionary<string, string>? Env = null) : ActionExecutionData;

    public sealed record Composite(
        List<CompositeStep> Steps) : ActionExecutionData;
}

public record CompositeStep(
    string? Uses,
    Dictionary<string, JsonElement?>? With = null,
    string? Run = null,
    string? Shell = null);
