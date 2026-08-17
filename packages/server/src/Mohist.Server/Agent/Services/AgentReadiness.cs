using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;

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
        if (modelState == PropertyState.Missing)
            return AgentReadinessKind.NeedsSetup;
        if (modelState == PropertyState.Invalid)
            return AgentReadinessKind.Unknown;
        if (AgentConfigSchema.ValidateRuntime(config) is not null)
            return AgentReadinessKind.NeedsSetup;
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

public sealed record AgentConnectionDispatchDecision(
    bool Accepted,
    string Kind,
    string? Reason,
    AgentExecutabilityResult? Executability = null,
    bool ConnectionUnavailable = false)
{
    public static AgentConnectionDispatchDecision For(string executability) =>
        For(new AgentExecutabilityResult(executability, [], null));

    public static AgentConnectionDispatchDecision For(AgentExecutabilityResult executability) => executability.State switch
    {
        AgentExecutabilityStates.NotConfigured => new(
            false,
            "agent_not_configured",
            "Agent setup is incomplete. Fix the Agent definition before dispatching a task.",
            executability),
        AgentExecutabilityStates.NotExecutable => new(
            false,
            "agent_not_executable",
            "The current Agent definition was rejected by its execution configuration. Update it before dispatching a task.",
            executability),
        AgentExecutabilityStates.Unknown => new(
            true,
            "accepted",
            "Agent executability is unknown; the task is accepted and awaiting Runner verification.",
            executability),
        _ => new(true, "accepted", null, executability),
    };

    public static AgentConnectionDispatchDecision ForConnection(AgentConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.DesiredState == DesiredStateKind.Disabled)
            return new(true, "accepted", null);

        if (connection.ConnectionHealth == ConnectionHealthKind.Degraded
            && SlackConnectionBackpressureReasons.IsBackpressureReason(connection.HealthReason))
        {
            return new(
                false,
                "backpressured",
                "This Slack Connection is backpressured; retry after pending deliveries drain.",
                ConnectionUnavailable: true);
        }

        if (connection.SetupProgress != SetupProgressKind.Complete
            || connection.ConnectionHealth is ConnectionHealthKind.Unhealthy or ConnectionHealthKind.Degraded
            || connection.OfflineGapAt is not null)
        {
            return new(
                false,
                "connection_unavailable",
                "This Slack Connection is not ready to accept new tasks.",
                ConnectionUnavailable: true);
        }

        return new(true, "accepted", null);
    }
}
