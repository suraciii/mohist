using System.Text.Json.Nodes;

namespace Mohist.Cli;

// Converts the dotted `<key>` argument used by the `variable` commands into the
// nested PATCH document the server already deep-merges. The merge treats a
// `null` leaf as a delete instruction and never persists it, so a single
// builder covers `set` (leaf = string or --value-json) and `unset`
// (leaf = JSON null).
//
// Validation runs locally before any HTTP call. Traversal through a non-object
// (array, scalar, null) is rejected — the only way to address such a position
// is a server-side primitive write, which the CLI does not own.
internal static class VariableKeyPath
{
    internal const string InvalidKeyMessage =
        "<key> must be a non-empty dot-separated path with no empty segments";

    // Wraps the caller's leaf in the nested `{ vars: ... }` or
    // `{ stages: { <stage>: { vars: ... } } }` PATCH envelope.
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

    // Emits the same nested envelope as a set, but with a JSON `null` leaf so
    // the merge treats it as a delete instruction.
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

    // Splits a dotted key into non-empty segments. Empty / whitespace-only
    // segments are rejected (so `a..b`, `.a`, `a.` all fail locally), but
    // surrounding whitespace is trimmed per segment so ` agent . model ` is
    // accepted. The caller is responsible for surfacing the local usage error
    // and skipping the HTTP call on failure.
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

    // Mirror of `BuildNested` for the unset path. The leaf is a literal JSON
    // `null`, assigned to a `JsonObject` indexer so the result encodes the
    // null value rather than a C# null reference (which `JsonNode.Parse("null")`
    // and `JsonValue.Create((object?)null)` both produce, breaking the merge).
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
