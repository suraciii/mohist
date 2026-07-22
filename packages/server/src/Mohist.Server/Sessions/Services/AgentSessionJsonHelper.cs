using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

public static class AgentSessionJsonHelper
{
    internal static readonly TimeSpan ActiveRuntimeEventWindow = TimeSpan.FromMinutes(5);
    public static string StatusName(AgentSession session, DateTime now) => session.Status.Activity switch
    {
        AgentSessionActivity.Active => "active",
        AgentSessionActivity.Unknown => "unknown",
        _ => "inactive",
    };

    public static DateTime LastActivityAt(AgentSession session) =>
        session.Status.LastDataAt ?? session.Status.BoundAt ?? session.Status.CreatedAt;

    public static AgentUsageSummary Usage(AgentSession session) =>
        session.Status.UsageSummary ?? new AgentUsageSummary();

    public static double? ContextUsagePercent(long? used, long? size)
    {
        if (used is null || size is null || size.Value <= 0 || used.Value < 0) return null;
        var ratio = (double)used.Value / size.Value;
        if (double.IsNaN(ratio) || double.IsInfinity(ratio)) return null;
        return Math.Round(Math.Clamp(ratio, 0d, 1d) * 100d, 2);
    }

    public static string? GetStringProp(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    public static string? GetStringProp(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object) return null;
        return element.Value.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    public static long? GetLongProp(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt64()
            : null;
    }

    public static double? GetDoubleProp(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetDouble()
            : null;
    }

    public static int? GetIntProp(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt32()
            : null;
    }

    public static int? GetIntProp(JsonElement? element, string name) =>
        element is null ? null : GetIntProp(element.Value, name);

    public static bool? GetBoolProp(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(prop.GetString(), out var parsed) ? parsed : null,
            _ => null,
        };
    }

    public static string? GetToolStringProp(JsonElement payload, string name)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("toolCall", out var toolCall))
        {
            var nested = GetStringProp(toolCall, name);
            if (nested is not null) return nested;
        }

        return GetStringProp(payload, name);
    }

    public static string? GetToolStringProp(JsonElement? payload, string name) =>
        payload is null ? null : GetToolStringProp(payload.Value, name);

    public static double? GetCostAmount(JsonElement payload)
    {
        var direct = GetDoubleProp(payload, "costAmount");
        if (direct is not null) return direct;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("cost", out var costProp)
            && costProp.ValueKind == JsonValueKind.Object
            ? GetDoubleProp(costProp, "amount")
            : null;
    }

    public static string? GetCostCurrency(JsonElement payload)
    {
        var direct = GetStringProp(payload, "costCurrency");
        if (direct is not null) return direct;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("cost", out var costProp)
            && costProp.ValueKind == JsonValueKind.Object
            ? GetStringProp(costProp, "currency")
            : null;
    }

    public static long? GetContextWindowUsed(JsonElement payload)
    {
        var direct = GetLongProp(payload, "contextWindowUsed");
        if (direct is not null) return direct;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("contextWindow", out var cwProp)
            && cwProp.ValueKind == JsonValueKind.Object
            ? GetLongProp(cwProp, "used")
            : null;
    }

    public static long? GetContextWindowSize(JsonElement payload)
    {
        var direct = GetLongProp(payload, "contextWindowSize");
        if (direct is not null) return direct;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("contextWindow", out var cwProp)
            && cwProp.ValueKind == JsonValueKind.Object
            ? GetLongProp(cwProp, "size")
            : null;
    }

    public static string ExtractText(string json)
    {
        try
        {
            var payload = JSON.DeserializeElement(json);
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("text", out var text))
                return text.GetString() ?? string.Empty;
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object && content.TryGetProperty("text", out var contentText))
                return contentText.GetString() ?? string.Empty;
            if (payload.ValueKind == JsonValueKind.String)
                return payload.GetString() ?? string.Empty;
        }
        catch
        {
        }
        return string.Empty;
    }

    public static JsonElement? ParsePayload(string json)
    {
        try
        {
            return JSON.DeserializeElement(json);
        }
        catch
        {
            return null;
        }
    }

    public static JsonElement ParsePayloadOrEmpty(string json) =>
        ParsePayload(json) ?? JsonDocument.Parse("{}").RootElement.Clone();

    public static string NormalizePromptKind(string? kind) => kind switch
    {
        "initial" or "task" or "retry" or "followup" or "recovery" or "legacy-missing" => kind,
        _ => "task"
    };

    public static string? ExtractCorrelationId(string json)
    {
        try
        {
            var payload = JSON.DeserializeElement(json);
            return GetStringProp(payload, "messageId")
                ?? GetStringProp(payload, "partId")
                ?? GetToolStringProp(payload, "toolCallId")
                ?? GetToolStringProp(payload, "id")
                ?? GetToolStringProp(payload, "callId");
        }
        catch
        {
            return null;
        }
    }
}
