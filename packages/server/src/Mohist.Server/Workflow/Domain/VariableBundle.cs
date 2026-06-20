using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
    /// base.Vars 与 override.Vars deep merge, base.Stages 与 override.Stages deep merge。
    /// </summary>
    public static VariableBundle Patch(VariableBundle? @base, VariableBundle? overlay)
    {
        if (@base is null) return overlay ?? Empty;
        if (overlay is null) return @base;

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
    /// Deep merge 两个 JsonElement (期望都是 object)。
    /// 后者覆盖前者同名 key; 嵌套对象递归合并。
    /// 非 object 类型时后者直接替换。
    /// </summary>
    public static JsonElement? DeepMerge(JsonElement? @base, JsonElement? overlay)
    {
        if (!overlay.HasValue) return @base;
        if (!@base.HasValue) return overlay.Value.Clone();

        if (@base.Value.ValueKind != JsonValueKind.Object)
            return overlay.Value.Clone();
        if (overlay.Value.ValueKind != JsonValueKind.Object)
            return overlay.Value.Clone();

        var node = JsonNode.Parse(@base.Value.GetRawText())?.AsObject();
        if (node is null)
            return overlay.Value.Clone();

        foreach (var property in overlay.Value.EnumerateObject())
        {
            var existing = node[property.Name];
            node[property.Name] = MergeNode(existing, property.Value);
        }

        return JSON.DeserializeElement(node.ToJsonString());
    }

    private static JsonNode? MergeNode(JsonNode? existing, JsonElement overlay)
    {
        if (existing is JsonObject existingObject && overlay.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in overlay.EnumerateObject())
                existingObject[property.Name] = MergeNode(existingObject[property.Name], property.Value);
            return existingObject;
        }

        return JsonNode.Parse(overlay.GetRawText());
    }

    private static Dictionary<string, StageVariables>? MergeStages(
        Dictionary<string, StageVariables>? @base,
        Dictionary<string, StageVariables>? overlay)
    {
        if (@base is null && overlay is null) return null;

        var result = new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase);

        if (@base is not null)
        {
            foreach (var (stage, stageVars) in @base)
                result[stage] = stageVars.Copy();
        }

        if (overlay is not null)
        {
            foreach (var (stage, stageVars) in overlay)
            {
                if (result.TryGetValue(stage, out var existing))
                {
                    result[stage] = new StageVariables(DeepMerge(existing.Vars, stageVars.Vars));
                }
                else
                {
                    result[stage] = new StageVariables(
                        stageVars.Vars.HasValue ? stageVars.Vars.Value.Clone() : null);
                }
            }
        }

        return result;
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
