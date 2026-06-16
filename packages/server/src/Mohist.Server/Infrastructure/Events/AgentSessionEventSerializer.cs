using System.Text.Json;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Infrastructure.Events;

internal static class AgentSessionEventSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Type(AgentSessionEvent payload) => Unwrap(payload).GetType().Name;

    /// <summary>
    /// CloudEvents 1.0.2 reverse-DNS <c>type</c> for the agent-session domain event.
    /// Mirrors <see cref="WorkflowEventSerializer.BusType"/>. Each aggregate
    /// event resolves to a stable reverse-DNS string so producers and consumers
    /// agree on the wire format.
    /// </summary>
    public static string BusType(AgentSessionEvent payload) => Unwrap(payload) switch
    {
        AgentSessionRuntimeBound => EventCatalog.ReverseDns.AgentSessionRuntimeBound,
        AgentSessionUsageRecorded => EventCatalog.ReverseDns.AgentSessionUsageRecorded,
        AgentSessionModelChanged => EventCatalog.ReverseDns.AgentSessionModelChanged,
        AgentSessionContextCompacted => EventCatalog.ReverseDns.AgentSessionContextCompacted,
        AgentSessionContextExhausted => EventCatalog.ReverseDns.AgentSessionContextExhausted,
        AgentSessionContextHealthUpdated => EventCatalog.ReverseDns.AgentSessionContextHealthUpdated,
        _ => throw new InvalidOperationException($"No CloudEvents type for {Unwrap(payload).GetType().Name}"),
    };

    public static JsonElement ToData(AgentSessionEvent payload) =>
        JsonSerializer.SerializeToElement(Unwrap(payload), JsonOptions);

    public static object Unwrap(AgentSessionEvent payload) => payload switch
    {
        AgentSessionRuntimeBound x => (object)x,
        AgentSessionUsageRecorded x => x,
        AgentSessionModelChanged x => x,
        AgentSessionContextCompacted x => x,
        AgentSessionContextExhausted x => x,
        AgentSessionContextHealthUpdated x => x,
        null => throw new InvalidOperationException("Null agent session event"),
    };
}
