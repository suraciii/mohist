using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Workflow.Domain;

internal static class VariableJsonMerge
{
    public static JsonElement? ApplyPatch(JsonElement? @base, JsonElement? patch)
    {
        if (!patch.HasValue) return @base;
        if (!@base.HasValue) return ClonePatchDocument(patch.Value);

        if (@base.Value.ValueKind != JsonValueKind.Object)
            return ClonePatchDocument(patch.Value);
        if (patch.Value.ValueKind != JsonValueKind.Object)
            return patch.Value.Clone();

        var node = JsonNode.Parse(@base.Value.GetRawText())?.AsObject();
        if (node is null)
            return ClonePatchDocument(patch.Value);

        ApplyObjectPatch(node, patch.Value);
        return JSON.DeserializeElement(node.ToJsonString());
    }

    public static JsonElement ClonePatchDocument(JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object)
            return patch.Clone();

        return JSON.DeserializeElement(ClonePatchObject(patch).ToJsonString());
    }

    private static JsonObject ClonePatchObject(JsonElement patch)
    {
        var node = new JsonObject();
        foreach (var property in patch.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
                continue;

            node[property.Name] = ClonePatchNode(property.Value);
        }
        return node;
    }

    private static JsonNode? ClonePatchNode(JsonElement patch)
    {
        if (patch.ValueKind == JsonValueKind.Object)
            return ClonePatchObject(patch);

        return JsonNode.Parse(patch.GetRawText());
    }

    private static void ApplyObjectPatch(JsonObject target, JsonElement patch)
    {
        foreach (var property in patch.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                target.Remove(property.Name);
                continue;
            }

            var existing = target[property.Name];
            if (existing is JsonObject existingObject && property.Value.ValueKind == JsonValueKind.Object)
            {
                ApplyObjectPatch(existingObject, property.Value);
            }
            else
            {
                target[property.Name] = ClonePatchNode(property.Value);
            }
        }
    }
}
