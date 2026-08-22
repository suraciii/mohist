using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Grains;

// JSON helper split from AgentSessionGrain to keep the main partial within
// the file-size ratchet after the retry turn-force change.
public sealed partial class AgentSessionGrain
{
    private static JsonElement SafeDeserialize(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return default;
        try
        {
            return JSON.DeserializeElement(payloadJson);
        }
        catch
        {
            return default;
        }
    }
}
