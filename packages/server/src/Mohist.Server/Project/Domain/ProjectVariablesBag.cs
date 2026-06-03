using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Project.Domain;

[GenerateSerializer]
public sealed record ProjectVariablesBag(
    [property: Id(0)] Dictionary<string, JsonElement?>? Vars = null,
    [property: Id(1)] Dictionary<string, ProjectStageVariablesBag?>? Stages = null)
{
    public static ProjectVariablesBag Empty { get; } = new();

    public static ProjectVariablesBag FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;

        try
        {
            return JsonSerializer.Deserialize<ProjectVariablesBag>(json, JsonOptions) ?? Empty;
        }
        catch
        {
            return Empty;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public ProjectVariablesBag PatchVar(string name, JsonElement value)
    {
        var vars = CopyVars(Vars);
        vars[name] = value.Clone();
        return this with { Vars = vars };
    }

    public ProjectVariablesBag DeleteVar(string name)
    {
        var vars = CopyVars(Vars);
        vars.Remove(name);
        return this with { Vars = vars.Count == 0 ? null : vars };
    }

    public ProjectVariablesBag PatchStageVar(string stage, string name, JsonElement value)
    {
        var stages = CopyStages(Stages);
        if (!stages.TryGetValue(stage, out var stageBag) || stageBag is null)
        {
            stageBag = new ProjectStageVariablesBag();
            stages[stage] = stageBag;
        }

        stageBag = stageBag.PatchVar(name, value);
        stages[stage] = stageBag;
        return this with { Stages = stages };
    }

    public ProjectVariablesBag DeleteStageVar(string stage, string name)
    {
        var stages = CopyStages(Stages);
        if (!stages.TryGetValue(stage, out var stageBag) || stageBag is null)
            return this with { Stages = stages.Count == 0 ? null : stages };

        stageBag = stageBag.DeleteVar(name);
        if (stageBag.IsEmpty)
            stages.Remove(stage);
        else
            stages[stage] = stageBag;

        return this with { Stages = stages.Count == 0 ? null : stages };
    }

    private static Dictionary<string, JsonElement?> CopyVars(Dictionary<string, JsonElement?>? source) =>
        source is null
            ? new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
            : source.ToDictionary(kv => kv.Key, kv => CloneElement(kv.Value), StringComparer.Ordinal);

    private static Dictionary<string, ProjectStageVariablesBag?> CopyStages(Dictionary<string, ProjectStageVariablesBag?>? source)
    {
        if (source is null)
            return new Dictionary<string, ProjectStageVariablesBag?>(StringComparer.OrdinalIgnoreCase);

        return source.ToDictionary(
            kv => kv.Key,
            kv => kv.Value is null ? null : kv.Value.Copy(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static JsonElement? CloneElement(JsonElement? value) => value.HasValue ? value.Value.Clone() : null;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

[GenerateSerializer]
public sealed record ProjectStageVariablesBag(
    [property: Id(0)] Dictionary<string, JsonElement?>? Vars = null)
{
    public bool IsEmpty => Vars is null || Vars.Count == 0;

    public ProjectStageVariablesBag PatchVar(string name, JsonElement value)
    {
        var vars = Vars is null
            ? new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
            : Vars.ToDictionary(kv => kv.Key, kv => kv.Value.HasValue ? kv.Value.Value.Clone() : (JsonElement?)null, StringComparer.Ordinal);
        vars[name] = value.Clone();
        return this with { Vars = vars };
    }

    public ProjectStageVariablesBag DeleteVar(string name)
    {
        if (Vars is null)
            return this;

        var vars = Vars.ToDictionary(kv => kv.Key, kv => kv.Value.HasValue ? kv.Value.Value.Clone() : (JsonElement?)null, StringComparer.Ordinal);
        vars.Remove(name);
        return this with { Vars = vars.Count == 0 ? null : vars };
    }

    public ProjectStageVariablesBag Copy() =>
        new(Vars?.ToDictionary(kv => kv.Key, kv => kv.Value.HasValue ? kv.Value.Value.Clone() : (JsonElement?)null, StringComparer.Ordinal));
}
