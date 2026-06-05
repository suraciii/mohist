using System.Text.Json;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Infrastructure.Events;

internal static class AgentSessionEventSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Type(AgentSessionEvent payload) => Unwrap(payload).GetType().Name;

    public static JsonElement ToData(AgentSessionEvent payload) =>
        JsonSerializer.SerializeToElement(Unwrap(payload), JsonOptions);

    public static AgentSessionEvent FromData(string type, JsonElement data) => type switch
    {
        nameof(AgentSessionStarted) => data.Deserialize<AgentSessionStarted>(JsonOptions)!,
        nameof(AgentSessionActivated) => data.Deserialize<AgentSessionActivated>(JsonOptions)!,
        nameof(AgentSessionUsageRecorded) => data.Deserialize<AgentSessionUsageRecorded>(JsonOptions)!,
        nameof(AgentSessionModelChanged) => data.Deserialize<AgentSessionModelChanged>(JsonOptions)!,
        nameof(AgentSessionCompleted) => data.Deserialize<AgentSessionCompleted>(JsonOptions)!,
        nameof(AgentSessionFailed) => data.Deserialize<AgentSessionFailed>(JsonOptions)!,
        nameof(AgentSessionCancelled) => data.Deserialize<AgentSessionCancelled>(JsonOptions)!,
        _ => throw new InvalidOperationException($"Unknown agent session event '{type}'"),
    };

    public static object Unwrap(AgentSessionEvent payload) => payload switch
    {
        AgentSessionStarted x => x,
        AgentSessionActivated x => x,
        AgentSessionUsageRecorded x => x,
        AgentSessionModelChanged x => x,
        AgentSessionCompleted x => x,
        AgentSessionFailed x => x,
        AgentSessionCancelled x => x,
    };
}
