using System.Text.Json;
using System.Text.Json.Nodes;
using Orleans;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Workflow.Domain;

/// <summary>
/// 变量束: 三层独立变量(project/issue/workflow-run)的统一类型。
/// 结构:
///   {
///     "vars":   { ... },                       &lt;- 全局变量
///     "stages": {                               &lt;- 按阶段变量
///       "plan":  { "vars": { ... } },
///       "build": { "vars": { ... } }
///     }
///   }
///
/// 支持两种写语义:
///   Set (PUT)   - 完整替换
///   Patch (PATCH) - deep merge
///
/// </summary>
[GenerateSerializer]
public sealed record VariableBundle(
    [property: Id(0)] JsonElement? Vars = null,
    [property: Id(1)] Dictionary<string, StageVariables>? Stages = null)
{
    public static readonly VariableBundle Empty = new();

    public static VariableBundle FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;

        try
        {
            var parsed = JsonSerializer.Deserialize<VariableBundle>(json, JsonOptions);
            return parsed ?? Empty;
        }
        catch
        {
            return Empty;
        }
    }

    public static VariableBundle FromElement(JsonElement? element)
    {
        if (!element.HasValue)
            return Empty;

        try
        {
            var parsed = JsonSerializer.Deserialize<VariableBundle>(element.Value.GetRawText(), JsonOptions);
            return parsed ?? Empty;
        }
        catch
        {
            return Empty;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public JsonElement ToElement() =>
        JSON.DeserializeElement(ToJson());

    /// <summary>
    /// Set 语义 - 完整替换整个 bundle。
    /// </summary>
    public static VariableBundle Set(VariableBundle bundle) => bundle;

    /// <summary>
    /// Patch 语义 - deep merge override 到 base。
    /// base.Vars 与 overlay.Vars deep merge, base.Stages 与 overlay.Stages deep merge。
    /// overlay 中 JSON null 的属性被视为显式删除 (保留未出现的属性)。
    /// </summary>
    public static VariableBundle Patch(VariableBundle? @base, VariableBundle? overlay)
    {
        if (overlay is null) return @base ?? Empty;
        @base ??= Empty;

        var mergedVars = DeepMerge(@base.Vars, overlay.Vars);
        var mergedStages = MergeStages(@base.Stages, overlay.Stages);

        return new VariableBundle(
            mergedVars,
            mergedStages is { Count: > 0 } ? mergedStages : null);
    }

    /// <summary>
    /// 链式 merge 多个 bundle, 后一个覆盖前一个。
    /// </summary>
    public static VariableBundle MergeAll(params VariableBundle?[] layers)
    {
        var result = Empty;
        foreach (var layer in layers)
            result = Patch(result, layer);
        return result;
    }

    /// <summary>
    /// Resolve the object that action templates see as `vars` for one stage.
    /// Stage variables override top-level variables via deep merge: a stage's
    /// null-valued key removes that key from the merged result (so resolution
    /// sees the key as absent rather than falling back to the top-level value).
    /// </summary>
    public JsonElement? ResolveStageVars(string? stage)
    {
        if (!Vars.HasValue && Stages is null) return null;

        JsonElement? effective = Vars.HasValue && Vars.Value.ValueKind == JsonValueKind.Object
            ? Vars.Value
            : JSON.DeserializeElement("{}");

        if (Stages is not null
            && !string.IsNullOrWhiteSpace(stage)
            && Stages.TryGetValue(stage, out var stageVars)
            && stageVars.Vars.HasValue
            && stageVars.Vars.Value.ValueKind == JsonValueKind.Object)
        {
            effective = DeepMerge(effective, stageVars.Vars.Value) ?? stageVars.Vars.Value.Clone();
        }

        return effective;
    }

    public static JsonElement GetByKeyPath(JsonElement? root, string? keyPath)
    {
        if (!root.HasValue || string.IsNullOrWhiteSpace(keyPath))
            return JSON.DeserializeElement("null");

        var current = root.Value;
        foreach (var segment in keyPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return JSON.DeserializeElement("null");
        }

        return current.Clone();
    }

    /// <summary>
    /// Deep merge 两个 JsonElement (期望都是 object)。
    /// 后者覆盖前者同名 key; 嵌套对象递归合并。
    /// 非 object 类型时后者直接替换。
    /// overlay 中 JSON null 的属性视为显式删除: 移除 base 中同名 key,
    /// 若 base 中不存在该 key 则保持不存在 (no-op)。
    /// </summary>
    public static JsonElement? DeepMerge(JsonElement? @base, JsonElement? overlay)
    {
        if (!overlay.HasValue) return @base;
        if (!@base.HasValue) return CloneOverlay(overlay.Value);

        if (@base.Value.ValueKind != JsonValueKind.Object)
            return CloneOverlay(overlay.Value);
        if (overlay.Value.ValueKind != JsonValueKind.Object)
            return overlay.Value.Clone();

        var node = JsonNode.Parse(@base.Value.GetRawText())?.AsObject();
        if (node is null)
            return CloneOverlay(overlay.Value);

        foreach (var property in overlay.Value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                node.Remove(property.Name);
                continue;
            }
            var existing = node[property.Name];
            if (existing is JsonObject existingObject && property.Value.ValueKind == JsonValueKind.Object)
            {
                MergeIntoObject(existingObject, property.Value);
            }
            else
            {
                node[property.Name] = CloneOverlayNode(property.Value);
            }
        }

        return JSON.DeserializeElement(node.ToJsonString());
    }

    private static JsonElement CloneOverlay(JsonElement overlay)
    {
        if (overlay.ValueKind != JsonValueKind.Object)
            return overlay.Clone();

        return JSON.DeserializeElement(CloneOverlayObject(overlay).ToJsonString());
    }

    private static JsonObject CloneOverlayObject(JsonElement overlay)
    {
        var node = new JsonObject();
        foreach (var property in overlay.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
                continue;
            node[property.Name] = CloneOverlayNode(property.Value);
        }
        return node;
    }

    private static JsonNode? CloneOverlayNode(JsonElement overlay)
    {
        if (overlay.ValueKind == JsonValueKind.Object)
            return CloneOverlayObject(overlay);

        return JsonNode.Parse(overlay.GetRawText());
    }

    private static void MergeIntoObject(JsonNode? existing, JsonElement overlay)
    {
        if (existing is not JsonObject existingObject || overlay.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in overlay.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                existingObject.Remove(property.Name);
                continue;
            }
            var nestedExisting = existingObject[property.Name];
            if (nestedExisting is JsonObject nestedObject && property.Value.ValueKind == JsonValueKind.Object)
            {
                MergeIntoObject(nestedObject, property.Value);
            }
            else
            {
                existingObject[property.Name] = CloneOverlayNode(property.Value);
            }
        }
    }

    private static Dictionary<string, StageVariables>? MergeStages(
        Dictionary<string, StageVariables>? @base,
        Dictionary<string, StageVariables>? overlay)
    {
        if (@base is null && overlay is null) return null;

        var stages = new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase);

        if (@base is not null)
        {
            foreach (var (stage, stageVars) in @base)
                stages[stage] = stageVars.Copy();
        }

        if (overlay is not null)
        {
            foreach (var (stage, stageVars) in overlay)
            {
                if (stages.TryGetValue(stage, out var existing))
                {
                    stages[stage] = new StageVariables(DeepMerge(existing.Vars, stageVars.Vars));
                }
                else
                {
                    stages[stage] = new StageVariables(
                        stageVars.Vars.HasValue ? CloneOverlay(stageVars.Vars.Value) : null);
                }
            }
        }

        return stages;
    }

    public static readonly JsonSerializerOptions JsonOptions = JSON.Options;
}

/// <summary>
/// 阶段变量: 每个 stage 的变量束。
/// </summary>
[GenerateSerializer]
public sealed record StageVariables(
    [property: Id(0)] JsonElement? Vars = null)
{
    public bool IsEmpty => !Vars.HasValue;

    public StageVariables Copy() =>
        new(Vars.HasValue ? Vars.Value.Clone() : null);
}
