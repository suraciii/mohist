using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static class VariableKeyPath
{
    internal const string InvalidKeyMessage =
        "<key> must be a non-empty dot-separated path with no empty segments";

    public static JsonObject BuildSetPatch(IReadOnlyList<string> segments, string? stage, JsonNode leaf)
    {
        var vars = BuildNested(segments, leaf, cloneLeaf: true);
        return stage is null
            ? new JsonObject { ["vars"] = vars }
            : new JsonObject
            {
                ["stages"] = new JsonObject
                {
                    [stage] = new JsonObject { ["vars"] = vars },
                },
            };
    }

    public static JsonObject BuildUnsetPatch(IReadOnlyList<string> segments, string? stage)
    {
        var vars = BuildNestedWithNullLeaf(segments);
        return stage is null
            ? new JsonObject { ["vars"] = vars }
            : new JsonObject
            {
                ["stages"] = new JsonObject
                {
                    [stage] = new JsonObject { ["vars"] = vars },
                },
            };
    }

    public static bool TryParse(string? key, out IReadOnlyList<string> segments, out string? error)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            segments = [];
            error = "<key> is required and must not be empty";
            return false;
        }

        var raw = key.Split('.', StringSplitOptions.None);
        if (raw.Length == 0)
        {
            segments = [];
            error = InvalidKeyMessage;
            return false;
        }

        var trimmed = new string[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            trimmed[i] = raw[i].Trim();
            if (string.IsNullOrEmpty(trimmed[i]))
            {
                segments = [];
                error = InvalidKeyMessage;
                return false;
            }
        }

        segments = trimmed;
        error = null;
        return true;
    }

    private static JsonNode BuildNested(IReadOnlyList<string> segments, JsonNode leaf, bool cloneLeaf)
    {
        JsonNode root = cloneLeaf ? leaf.DeepClone() : leaf;
        for (var i = segments.Count - 1; i >= 0; i--)
        {
            root = new JsonObject
            {
                [segments[i]] = root,
            };
        }
        return root;
    }

    private static JsonNode BuildNestedWithNullLeaf(IReadOnlyList<string> segments)
    {
        var leafHolder = new JsonObject();
        var current = leafHolder;
        var count = segments.Count;
        for (var i = 0; i < count - 1; i++)
        {
            var next = new JsonObject();
            current[segments[i]] = next;
            current = next;
        }
        current[segments[count - 1]] = null;
        return leafHolder;
    }
}
