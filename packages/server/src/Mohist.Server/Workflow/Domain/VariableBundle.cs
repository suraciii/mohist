using System.Text.Json;
using Orleans;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Workflow.Domain;

/// <summary>
/// Variables shared by project, issue, and workflow-run scopes.
///
/// <para>
/// A bundle carries two parallel shapes: the explicit value tree
/// (<see cref="Vars"/> + <see cref="Stages"/>) and a tree of
/// <see cref="DefaultVars"/> + <see cref="DefaultStages"/> whose entries are
/// flagged as initialization defaults. The defaults resolve at the bottom of
/// the resource precedence stack so an explicit Project, Issue, or Run write
/// always wins; once an explicit write targets a default key the runner clears
/// that key from the defaults tree and the written value follows the standard
/// top-level / stage precedence rules.
/// </para>
/// </summary>
[GenerateSerializer]
public sealed record VariableBundle(
    [property: Id(0)] JsonElement? Vars = null,
    [property: Id(1)] Dictionary<string, StageVariables>? Stages = null,
    [property: Id(2)] JsonElement? DefaultVars = null,
    [property: Id(3)] Dictionary<string, StageVariables>? DefaultStages = null)
{
    public static readonly VariableBundle Empty = new();

    public bool HasExplicitContent => Vars.HasValue || Stages is { Count: > 0 };
    public bool HasDefaultContent => DefaultVars.HasValue || DefaultStages is { Count: > 0 };

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

    public static VariableBundle Set(VariableBundle bundle) => bundle;

    public static VariableBundle Patch(VariableBundle? @base, VariableBundle? overlay)
    {
        if (overlay is null) return @base ?? Empty;
        @base ??= Empty;

        var mergedVars = VariableJsonMerge.ApplyPatch(@base.Vars, overlay.Vars);
        var mergedStages = MergeStages(@base.Stages, overlay.Stages);
        var mergedDefaultVars = VariableJsonMerge.ApplyPatch(@base.DefaultVars, overlay.DefaultVars);
        var mergedDefaultStages = MergeStages(@base.DefaultStages, overlay.DefaultStages);

        return new VariableBundle(
            mergedVars,
            mergedStages is { Count: > 0 } ? mergedStages : null,
            mergedDefaultVars,
            mergedDefaultStages is { Count: > 0 } ? mergedDefaultStages : null);
    }

    public static VariableBundle MergeAll(params VariableBundle?[] layers)
    {
        var result = Empty;
        foreach (var layer in layers)
            result = Patch(result, layer);
        return result;
    }
    /// <summary>
    /// Returns a new bundle whose default tree has every key found in
    /// <paramref name="explicitOverlay"/>'s explicit tree removed. The
    /// default tree is otherwise left untouched. Used when an explicit
    /// write to a key supersedes its initialization default: the caller
    /// drops the default marker so subsequent resolutions consult the
    /// explicit value under the standard precedence rules.
    /// </summary>
    public VariableBundle ClearDefaultsCoveredByExplicit(VariableBundle explicitOverlay)
    {
        if (!HasDefaultContent) return this;

        var topLevelKeys = DefaultVars.HasValue && DefaultVars.Value.ValueKind == JsonValueKind.Object
            ? CollectTopLevelKeys(DefaultVars.Value)
            : null;
        var coveredTopLevel = IntersectKeysWithExplicit(topLevelKeys, explicitOverlay.Vars);
        var stageKeys = DefaultStages is { Count: > 0 }
            ? CollectStageKeys(DefaultStages)
            : null;
        var coveredStageKeys = IntersectStageKeysWithExplicit(stageKeys, explicitOverlay.Stages);

        if ((coveredTopLevel is null || coveredTopLevel.Count == 0)
            && (coveredStageKeys is null || coveredStageKeys.Count == 0))
        {
            return this;
        }

        var newDefaultVars = DefaultVars.HasValue
            ? StripTopLevelKeys(DefaultVars.Value, coveredTopLevel)
            : DefaultVars;
        var newDefaultStages = DefaultStages is { Count: > 0
        }
            ? StripStageKeys(DefaultStages, coveredStageKeys)
            : DefaultStages;

        var varsChanged = !NullableJsonElementEquals(newDefaultVars, DefaultVars);
        var stagesChanged = !ReferenceEquals(newDefaultStages, DefaultStages);

        if (!varsChanged && !stagesChanged) return this;

        return new VariableBundle(
            Vars,
            Stages,
            varsChanged ? newDefaultVars : DefaultVars,
            stagesChanged ? newDefaultStages : DefaultStages);
    }

    private static HashSet<string>? IntersectKeysWithExplicit(
        HashSet<string>? defaultKeys,
        JsonElement? explicitVars)
    {
        if (defaultKeys is null || defaultKeys.Count == 0) return null;
        if (!explicitVars.HasValue || explicitVars.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in explicitVars.Value.EnumerateObject())
        {
            if (defaultKeys.Contains(property.Name)) result.Add(property.Name);
        }
        return result.Count == 0 ? null : result;
    }

    private static Dictionary<string, HashSet<string>>? IntersectStageKeysWithExplicit(
        Dictionary<string, HashSet<string>>? defaultStageKeys,
        Dictionary<string, StageVariables>? explicitStages)
    {
        if (defaultStageKeys is null || defaultStageKeys.Count == 0) return null;
        if (explicitStages is null) return null;

        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (stage, defaultKeysForStage) in defaultStageKeys)
        {
            if (!explicitStages.TryGetValue(stage, out var explicitStage)) continue;
            if (!explicitStage.Vars.HasValue
                || explicitStage.Vars.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var intersected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in explicitStage.Vars.Value.EnumerateObject())
            {
                if (defaultKeysForStage.Contains(property.Name)) intersected.Add(property.Name);
            }
            if (intersected.Count > 0) result[stage] = intersected;
        }
        return result.Count == 0 ? null : result;
    }

    public JsonElement? ResolveStageVars(string? stage)
    {
        // The Vars / Stages trees are assumed to already have the
        // marked-default values merged in at the bottom of the resource
        // precedence stack (defaults -> project -> issue -> explicit run).
        // `ResolveStageVars` simply combines the merged top-level values
        // with the merged selected-stage overlay.
        var hasTop = Vars.HasValue && Vars.Value.ValueKind == JsonValueKind.Object;
        var hasDefaultTop = DefaultVars.HasValue && DefaultVars.Value.ValueKind == JsonValueKind.Object;
        var hasStage = !string.IsNullOrWhiteSpace(stage)
            && Stages is not null
            && Stages.TryGetValue(stage, out var stageVars)
            && stageVars.Vars.HasValue
            && stageVars.Vars.Value.ValueKind == JsonValueKind.Object;
        var hasDefaultStage = !string.IsNullOrWhiteSpace(stage)
            && DefaultStages is not null
            && DefaultStages.TryGetValue(stage, out var defaultStageVars)
            && defaultStageVars.Vars.HasValue
            && defaultStageVars.Vars.Value.ValueKind == JsonValueKind.Object;

        if (!hasTop && !hasDefaultTop && !hasStage && !hasDefaultStage) return null;

        JsonElement effective = hasTop
            ? Vars!.Value
            : (hasDefaultTop ? DefaultVars!.Value : JSON.DeserializeElement("{}"));

        if (hasDefaultStage)
        {
            var mergedDefaultStage = VariableJsonMerge.ApplyPatch(effective, DefaultStages![stage!].Vars!.Value);
            effective = mergedDefaultStage ?? DefaultStages[stage!].Vars!.Value.Clone();
        }

        if (hasStage)
        {
            var mergedStage = VariableJsonMerge.ApplyPatch(effective, Stages![stage!].Vars!.Value);
            effective = mergedStage ?? Stages[stage!].Vars!.Value.Clone();
        }

        return effective;
    }

    public JsonElement? ResolveStageOverlay(string? stage)
    {
        if (HasDefaultStage(stage) || HasExplicitStage(stage))
        {
            return ResolveStageVars(stage);
        }
        return null;
    }

    public bool HasExplicitStage(string? stage)
    {
        if (Stages is null || string.IsNullOrWhiteSpace(stage)) return false;
        return Stages.TryGetValue(stage, out var stageVars)
            && stageVars.Vars.HasValue
            && stageVars.Vars.Value.ValueKind == JsonValueKind.Object;
    }

    public bool HasDefaultStage(string? stage)
    {
        if (DefaultStages is null || string.IsNullOrWhiteSpace(stage)) return false;
        return DefaultStages.TryGetValue(stage, out var stageVars)
            && stageVars.Vars.HasValue
            && stageVars.Vars.Value.ValueKind == JsonValueKind.Object;
    }

    private JsonElement? ResolveExplicitStage(string? stage)
    {
        if (Stages is null || string.IsNullOrWhiteSpace(stage)) return null;
        return Stages.TryGetValue(stage, out var stageVars)
            && stageVars.Vars.HasValue
            && stageVars.Vars.Value.ValueKind == JsonValueKind.Object
            ? stageVars.Vars.Value
            : null;
    }

    private JsonElement? ResolveDefaultStage(string? stage)
    {
        if (DefaultStages is null || string.IsNullOrWhiteSpace(stage)) return null;
        return DefaultStages.TryGetValue(stage, out var stageVars)
            && stageVars.Vars.HasValue
            && stageVars.Vars.Value.ValueKind == JsonValueKind.Object
            ? stageVars.Vars.Value
            : null;
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

    public static JsonElement? DeepMerge(JsonElement? @base, JsonElement? overlay) =>
        VariableJsonMerge.ApplyPatch(@base, overlay);

    private static HashSet<string> CollectTopLevelKeys(JsonElement element)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object) return keys;
        foreach (var property in element.EnumerateObject())
            keys.Add(property.Name);
        return keys;
    }

    private static Dictionary<string, HashSet<string>>? CollectStageKeys(Dictionary<string, StageVariables> stages)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (stage, stageVars) in stages)
        {
            if (!stageVars.Vars.HasValue || stageVars.Vars.Value.ValueKind != JsonValueKind.Object) continue;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in stageVars.Vars.Value.EnumerateObject())
                keys.Add(property.Name);
            if (keys.Count > 0) result[stage] = keys;
        }
        return result.Count == 0 ? null : result;
    }

    private static JsonElement? StripTopLevelKeys(JsonElement element, HashSet<string>? keys)
    {
        if (keys is null || keys.Count == 0) return element;
        if (element.ValueKind != JsonValueKind.Object) return element;
        var dict = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (keys.Contains(property.Name)) continue;
            dict[property.Name] = property.Value.Clone();
        }
        return JsonSerializer.SerializeToElement(dict, JsonOptions);
    }

    private static Dictionary<string, StageVariables>? StripStageKeys(
        Dictionary<string, StageVariables> stages,
        Dictionary<string, HashSet<string>>? keys)
    {
        if (keys is null || keys.Count == 0) return stages;
        var changed = false;
        var result = new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase);
        foreach (var (stage, stageVars) in stages)
        {
            if (!keys.TryGetValue(stage, out var strip))
            {
                result[stage] = stageVars;
                continue;
            }
            if (!stageVars.Vars.HasValue || stageVars.Vars.Value.ValueKind != JsonValueKind.Object)
            {
                result[stage] = stageVars;
                continue;
            }
            var stripped = StripTopLevelKeys(stageVars.Vars.Value, strip);
            changed = true;
            result[stage] = new StageVariables(stripped);
        }
        return changed ? result : stages;
    }

    private static bool NullableJsonElementEquals(JsonElement? a, JsonElement? b)
    {
        if (!a.HasValue && !b.HasValue) return true;
        if (!a.HasValue || !b.HasValue) return false;
        return a.Value.GetRawText() == b.Value.GetRawText();
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
                    stages[stage] = new StageVariables(VariableJsonMerge.ApplyPatch(existing.Vars, stageVars.Vars));
                }
                else
                {
                    stages[stage] = new StageVariables(
                        stageVars.Vars.HasValue ? VariableJsonMerge.ClonePatchDocument(stageVars.Vars.Value) : null);
                }
            }
        }

        return stages;
    }

    public static readonly JsonSerializerOptions JsonOptions = JSON.Options;
}

[GenerateSerializer]
public sealed record StageVariables(
    [property: Id(0)] JsonElement? Vars = null)
{
    public bool IsEmpty => !Vars.HasValue;

    public StageVariables Copy() =>
        new(Vars.HasValue ? Vars.Value.Clone() : null);
}
