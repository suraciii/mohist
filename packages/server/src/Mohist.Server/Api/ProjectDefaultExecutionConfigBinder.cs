using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Api;

internal static class ProjectDefaultExecutionConfigBinder
{
    internal static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "runtime",
        "model",
        "variant",
    };

    internal static async ValueTask<ProjectDefaultExecutionConfigBody?> BindAsync(HttpContext context)
    {
        try
        {
            return await BindCoreAsync(context);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async ValueTask<ProjectDefaultExecutionConfigBody> BindCoreAsync(HttpContext context)
    {
        var raw = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, JSON.Options);
        if (raw.ValueKind != JsonValueKind.Object)
            throw new JsonException("the default execution configuration must be a JSON object");

        var undeclared = new List<string>();
        foreach (var property in raw.EnumerateObject())
        {
            if (!AllowedFields.Contains(property.Name))
                undeclared.Add(property.Name);
        }

        return new ProjectDefaultExecutionConfigBody(
            Runtime: ReadString(raw, "runtime"),
            Model: ReadString(raw, "model"),
            Variant: ReadString(raw, "variant"),
            UndeclaredFields: undeclared);
    }

    private static string? ReadString(JsonElement raw, string name)
    {
        if (!raw.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new JsonException($"{name} must be a string");
        return value.GetString();
    }
}
