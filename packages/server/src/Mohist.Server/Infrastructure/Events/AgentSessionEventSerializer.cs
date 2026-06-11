using System.Text.Json;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Infrastructure.Events;

internal static class AgentSessionEventSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Type(AgentSessionEvent payload) => Unwrap(payload).GetType().Name;

    /// <summary>
    /// CloudEvents 1.0.2 reverse-DNS <c>type</c> for the agent-session domain event.
    /// Mirrors <see cref="WorkflowEventSerializer.BusType"/>. Lifecycle variants
    /// (Started / Activated / Completed / Failed / Cancelled / StatusChanged) each
    /// resolve to a stable reverse-DNS string so producers and consumers agree
    /// on the wire format.
    /// </summary>
    public static string BusType(AgentSessionEvent payload) => Unwrap(payload) switch
    {
        AgentSessionStarted => EventCatalog.ReverseDns.AgentSessionStarted,
        AgentSessionActivated => EventCatalog.ReverseDns.AgentSessionActivated,
        AgentSessionCompleted => EventCatalog.ReverseDns.AgentSessionCompleted,
        AgentSessionFailed => EventCatalog.ReverseDns.AgentSessionFailed,
        AgentSessionCancelled => EventCatalog.ReverseDns.AgentSessionCancelled,
        AgentSessionStatusChanged => EventCatalog.ReverseDns.AgentSessionStatusChanged,
        _ => throw new InvalidOperationException($"No CloudEvents type for {Unwrap(payload).GetType().Name}"),
    };

    public static JsonElement ToData(AgentSessionEvent payload) =>
        JsonSerializer.SerializeToElement(Unwrap(payload), JsonOptions);

    public static object Unwrap(AgentSessionEvent payload) => payload switch
    {
        AgentSessionStarted x => (object)x,
        AgentSessionActivated x => x,
        AgentSessionUsageRecorded x => x,
        AgentSessionModelChanged x => x,
        AgentSessionCompleted x => x,
        AgentSessionFailed x => x,
        AgentSessionCancelled x => x,
        AgentSessionStatusChanged x => x,
        null => throw new InvalidOperationException("Null agent session event"),
    };
}
