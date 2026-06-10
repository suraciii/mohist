using System.Text.Json;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Infrastructure.Events;

internal static class AgentSessionEventSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Type(AgentSessionEvent payload) => Unwrap(payload).GetType().Name;

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
        null => throw new InvalidOperationException("Null agent session event"),
    };
}
