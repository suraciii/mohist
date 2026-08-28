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

    public static void SetPath(JsonObject target, string path, JsonElement value)
    {
        var segments = path.Split('.');
        var current = target;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var segment = segments[index];
            if (current[segment] is not JsonObject child)
            {
                child = new JsonObject();
                current[segment] = child;
            }
            current = child;
        }

        // An explicit null in the patch document is the deletion
        // instruction consumed by ApplyObjectPatch; the indexer's null
        // assignment would drop the key instead, so the null goes through
        // Add. Nested nulls survive the raw-text clone below.
        if (value.ValueKind == JsonValueKind.Null)
        {
            current.Remove(segments[^1]);
            current.Add(segments[^1], null);
        }
        else
        {
            current[segments[^1]] = JsonNode.Parse(value.GetRawText());
        }
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
