using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Api;

internal static class RoutingRuleUpdateRequestBinder
{
    internal static async ValueTask<RoutingRuleUpdateRequest?> BindAsync(HttpContext context)
    {
        var raw = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, JSON.Options);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (raw.ValueKind == JsonValueKind.Object)
        {
            if (raw.TryGetProperty(RoutingRulePatchFields.Name, out _)) fields.Add(RoutingRulePatchFields.Name);
            if (raw.TryGetProperty(RoutingRulePatchFields.Match, out _)) fields.Add(RoutingRulePatchFields.Match);
            if (raw.TryGetProperty(RoutingRulePatchFields.AgentId, out _)) fields.Add(RoutingRulePatchFields.AgentId);
            if (raw.TryGetProperty(RoutingRulePatchFields.ResponsePrompt, out _)) fields.Add(RoutingRulePatchFields.ResponsePrompt);
            if (raw.TryGetProperty(RoutingRulePatchFields.Continue, out _)) fields.Add(RoutingRulePatchFields.Continue);
        }
        return new RoutingRuleUpdateRequest(
            GetString(raw, "name"), GetString(raw, "match"), GetString(raw, "agentId"),
            GetString(raw, "responsePrompt"), GetBool(raw, "continue"), fields, raw);
    }

    private static string? GetString(JsonElement raw, string name) =>
        raw.ValueKind == JsonValueKind.Object
            && raw.TryGetProperty(name, out var value)
            && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static bool? GetBool(JsonElement raw, string name) =>
        raw.ValueKind == JsonValueKind.Object
            && raw.TryGetProperty(name, out var value)
            && value.ValueKind != JsonValueKind.Null
            && value.ValueKind == JsonValueKind.True
                ? true
                : raw.ValueKind == JsonValueKind.Object
                    && raw.TryGetProperty(name, out value)
                    && value.ValueKind == JsonValueKind.False
                        ? false
                        : null;
}
