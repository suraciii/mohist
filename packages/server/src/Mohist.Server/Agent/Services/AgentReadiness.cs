using System.Text.Json;
using Mohist.Server.Agent.Domain;

namespace Mohist.Server.Agent.Services;

public static class AgentReadinessDeriver
{
    public static string Derive(JsonElement? agentConfig)
    {
        if (agentConfig is null)
            return AgentReadinessKind.NeedsSetup;

        var config = agentConfig.Value;
        if (config.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return AgentReadinessKind.NeedsSetup;
        if (config.ValueKind != JsonValueKind.Object)
            return AgentReadinessKind.Unknown;

        var modelState = ReadStringState(config, "model");
        var runtimeState = ReadStringState(config, "runtime");
        if (modelState == PropertyState.Missing || runtimeState == PropertyState.Missing)
            return AgentReadinessKind.NeedsSetup;
        if (modelState == PropertyState.Invalid || runtimeState == PropertyState.Invalid)
            return AgentReadinessKind.Unknown;
        return AgentReadinessKind.Ready;
    }

    private static PropertyState ReadStringState(JsonElement config, string propertyName)
    {
        if (!config.TryGetProperty(propertyName, out var property))
            return PropertyState.Missing;
        return property.ValueKind == JsonValueKind.String
            ? string.IsNullOrWhiteSpace(property.GetString()) ? PropertyState.Missing : PropertyState.Present
            : PropertyState.Invalid;
    }

    private enum PropertyState
    {
        Missing,
        Present,
        Invalid,
    }
}

public sealed record AgentConnectionDispatchDecision(bool Accepted, string Kind, string? Reason)
{
    public static AgentConnectionDispatchDecision For(string readiness) => readiness switch
    {
        AgentReadinessKind.NeedsSetup => new(
            false,
            "rejected",
            "Agent setup is incomplete: configure both a model and runtime before dispatching a task."),
        AgentReadinessKind.Unknown => new(
            true,
            "accepted",
            "Agent readiness is unknown; the task is accepted and awaiting Runner verification."),
        _ => new(true, "accepted", null),
    };
}
