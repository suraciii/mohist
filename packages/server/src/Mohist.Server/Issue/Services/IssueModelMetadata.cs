using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Issue.Services;

/// <summary>
/// Applies issue-level model metadata (<c>model</c>, <c>variant</c>,
/// <c>stageModels</c>, <c>stageModelVariants</c>) onto an issue's workflow
/// profile variables. The issue profile's <c>Variables.vars.agent</c> and
/// <c>Variables.stages.&lt;stage&gt;.vars.agent</c> are the single source of
/// truth for agent config at dispatch time (per the workflow-engine spec).
///
/// Variant is bound to its model: clearing a model atomically clears its
/// bound variant. The helper enforces:
///   1. <c>provider/model</c> format on every model value (HTTP 400 otherwise).
///   2. Clear-on-clear: a null/empty model removes the bound variant.
///   3. Variant is preserved across reads when its model is preserved.
///   4. Per-stage variants track per-stage model clears independently.
///
/// The agent block at <c>vars.agent</c> and at each stage's
/// <c>stages.&lt;stage&gt;.vars.agent</c> is treated as a SET target, not a
/// PATCH target — keys removed from the agent dict must not reappear because
/// the existing bundle's agent block had them. <see cref="VariableBundle.Patch"/>
/// is a deep merge that now treats overlay-null keys as explicit deletions,
/// but the model/variant helper still composes the agent dict directly to
/// keep the "set-replace the inner agent block" semantics intact, preserving
/// non-agent keys (and untouched stages) while replacing the agent block.
///
/// The route handler explicitly tracks JSON presence for each field and
/// constructs a <see cref="ModelMetadataPatch"/>; absent fields are passed
/// as <c>null</c> while present-but-null fields are represented by
/// <see cref="ModelMetadataPatch.ClearModel"/>, <see cref="ModelMetadataPatch.ClearVariant"/>,
/// <see cref="ModelMetadataPatch.ClearStageModels"/>, and
/// <see cref="ModelMetadataPatch.ClearStageModelVariants"/>. This lets the
/// helper honor the "present-but-null = clear" semantic for these fields
/// without conflating it with the conventional "absent = no change" semantic
/// used by the other DTO fields.
/// </summary>
public static class IssueModelMetadata
{
    /// <summary>
    /// Splits a model identifier at the first <c>/</c>: the segment before
    /// must be a non-empty, non-whitespace <c>provider</c>; the segment
    /// after must be a non-empty, non-whitespace <c>model ID</c> that may
    /// itself contain further <c>/</c> characters (e.g.
    /// <c>openrouter/vendor/family/model</c>). Empty/whitespace values
    /// are NOT rejected here — they mean "no model" and are cleared atomically.
    /// </summary>
    private static readonly Regex ProviderModelFormat = new(@"^[^/\s]+/\S+$", RegexOptions.Compiled);

    /// <summary>
    /// Validate that a model string conforms to <c>provider/model</c> format
    /// where the model ID may contain additional <c>/</c> segments. Returns
    /// null on success, or a user-facing error message on failure.
    /// Null/whitespace <paramref name="model"/> is allowed (means clear).
    /// </summary>
    public static string? ValidateModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        return ProviderModelFormat.IsMatch(model)
            ? null
            : $"Model '{model}' is invalid; expected 'provider/model' format.";
    }

    /// <summary>
    /// A tri-state patch entry: <c>Absent</c> (the request did not mention
    /// the field), <c>Clear</c> (the request explicitly nulled the field),
    /// or <c>Set(value)</c> (the request provided a non-null value).
    /// </summary>
    public readonly struct FieldPatch<T> where T : class
    {
        public FieldPatchKind Kind { get; }
        public T? Value { get; }

        private FieldPatch(FieldPatchKind kind, T? value)
        {
            Kind = kind;
            Value = value;
        }

        public static FieldPatch<T> Absent => new(FieldPatchKind.Absent, null);
        public static FieldPatch<T> Clear => new(FieldPatchKind.Clear, null);
        public static FieldPatch<T> Set(T value) => new(FieldPatchKind.Set, value);
    }

    public enum FieldPatchKind { Absent, Clear, Set }

    /// <summary>
    /// A patch describing every model-metadata field the route handler saw
    /// in the request body, with explicit presence tracking so the helper
    /// can distinguish "absent" from "explicit clear".
    /// </summary>
    public sealed record ModelMetadataPatch(
        FieldPatch<string> Model,
        FieldPatch<string> ModelVariant,
        FieldPatch<IReadOnlyDictionary<string, string>> StageModels,
        FieldPatch<IReadOnlyDictionary<string, string>> StageModelVariants)
    {
        public static ModelMetadataPatch None { get; } = new(
            FieldPatch<string>.Absent,
            FieldPatch<string>.Absent,
            FieldPatch<IReadOnlyDictionary<string, string>>.Absent,
            FieldPatch<IReadOnlyDictionary<string, string>>.Absent);

        public bool TouchesAnyField =>
            Model.Kind != FieldPatchKind.Absent
            || ModelVariant.Kind != FieldPatchKind.Absent
            || StageModels.Kind != FieldPatchKind.Absent
            || StageModelVariants.Kind != FieldPatchKind.Absent;
    }

    /// <summary>
    /// Apply the supplied model-metadata patch to the issue's stored
    /// variables bundle. Returns the new bundle. The route handler is
    /// responsible for building <paramref name="patch"/> with explicit
    /// presence tracking; fields marked <see cref="FieldPatchKind.Absent"/>
    /// are not touched, fields marked <see cref="FieldPatchKind.Clear"/>
    /// have their bound variants atomically cleared, and fields marked
    /// <see cref="FieldPatchKind.Set"/> are SET-replaced with the new value.
    /// </summary>
    public static VariableBundle ApplyModelMetadata(VariableBundle bundle, ModelMetadataPatch patch)
    {
        if (bundle is null) throw new ArgumentNullException(nameof(bundle));
        if (patch is null) throw new ArgumentNullException(nameof(patch));

        if (!patch.TouchesAnyField) return bundle;

        var topChanged = TryMutateAgent(bundle.Vars, patch.Model, patch.ModelVariant, out var newVars);
        var stagesChanged = TryMutateStages(bundle.Stages, patch.StageModels, patch.StageModelVariants, out var newStages);

        if (!topChanged && !stagesChanged) return bundle;

        return new VariableBundle(
            Vars: topChanged ? newVars : bundle.Vars,
            Stages: stagesChanged ? newStages : bundle.Stages);
    }

    /// <summary>
    /// Validate the model metadata in a create/update request. Returns null
    /// on success, or the first user-facing error message.
    /// </summary>
    public static string? Validate(
        string? model,
        IReadOnlyDictionary<string, string>? stageModels)
    {
        var modelError = ValidateModel(model);
        if (modelError is not null) return modelError;

        if (stageModels is not null)
        {
            foreach (var (stage, value) in stageModels)
            {
                if (value is null) continue;
                var stageError = ValidateModel(value);
                if (stageError is not null)
                    return $"stageModels.{stage}: {stageError}";
            }
        }

        return null;
    }

    /// <summary>
    /// Validate the open-shape <c>agentConfig</c> body supplied at issue
    /// create/update. The converged surface accepts only
    /// <c>{model, variant}</c>; any other key (or any legacy runtime/liveness
    /// key explicitly named in <see cref="AgentConfigSchema.ForbiddenKeys"/>)
    /// is rejected at the API boundary so it never reaches persistence.
    /// Returns <c>null</c> when no offending key is found, otherwise the
    /// first user-facing error message.
    /// </summary>
    public static string? ValidateAgentConfig(JsonElement? agentConfig) =>
        AgentConfigSchema.Validate(agentConfig);

    /// <summary>
    /// Compute the new top-level <c>vars</c> element with a SET-replace
    /// <c>agent</c> block. Returns <c>true</c> when the agent block was
    /// touched so the caller persists it.
    /// </summary>
    private static bool TryMutateAgent(
        JsonElement? vars,
        FieldPatch<string> model,
        FieldPatch<string> modelVariant,
        out JsonElement? newVars)
    {
        newVars = null;

        if (model.Kind == FieldPatchKind.Absent && modelVariant.Kind == FieldPatchKind.Absent)
            return false;

        var agentVars = ExtractAgent(vars);
        var previousModel = agentVars.TryGetValue("model", out var m) ? AsString(m) : null;
        var previousVariant = agentVars.TryGetValue("variant", out var v) ? AsString(v) : null;

        var agentChanged = MutateAgentDict(agentVars, model, modelVariant, previousModel, previousVariant);
        if (!agentChanged) return false;

        newVars = ComposeVars(vars, agentVars);
        return true;
    }

    /// <summary>
    /// Compute the new <c>stages</c> dict, preserving untouched stages and
    /// SET-replacing the agent block of touched stages. Returns <c>true</c>
    /// when at least one stage's agent block was mutated.
    /// </summary>
    private static bool TryMutateStages(
        Dictionary<string, StageVariables>? existing,
        FieldPatch<IReadOnlyDictionary<string, string>> stageModels,
        FieldPatch<IReadOnlyDictionary<string, string>> stageModelVariants,
        out Dictionary<string, StageVariables>? newStages)
    {
        newStages = null;

        if (stageModels.Kind == FieldPatchKind.Absent && stageModelVariants.Kind == FieldPatchKind.Absent)
            return false;

        var touched = false;
        var result = new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase);

        if (existing is not null)
        {
            foreach (var kvp in existing)
                result[kvp.Key] = kvp.Value.Copy();
        }

        // Stage-wide clear: when the caller sends Clear on the whole map
        // (e.g. {"stageModels": null}), every existing stage's agent block
        // is dropped (model and variant atomically).
        var clearAllStages = stageModels.Kind == FieldPatchKind.Clear
            || stageModelVariants.Kind == FieldPatchKind.Clear;

        if (clearAllStages)
        {
            if (result.Count == 0) return false;
            foreach (var key in result.Keys.ToArray())
            {
                var emptyAgent = new Dictionary<string, object?>(StringComparer.Ordinal);
                var stageVarsElement = JsonSerializer.SerializeToElement(
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["agent"] = emptyAgent },
                    WorkflowVariableJson.Options);
                result[key] = new StageVariables(stageVarsElement);
                touched = true;
            }
            if (!touched) return false;
            newStages = result;
            return true;
        }

        // Per-stage mutation: collect stage names from existing + the patch maps.
        var stageNames = CollectStageNames(existing, stageModels, stageModelVariants);

        foreach (var stage in stageNames)
        {
            StageVariables? existingStage = null;
            existing?.TryGetValue(stage, out existingStage);
            var stageAgent = ExtractAgent(existingStage?.Vars);

            string? prevModel = stageAgent.TryGetValue("model", out var pm) ? AsString(pm) : null;
            string? prevVariant = stageAgent.TryGetValue("variant", out var pv) ? AsString(pv) : null;

            var perStageModel = ExtractFieldPatch(stageModels, stage);
            var perStageVariant = ExtractFieldPatch(stageModelVariants, stage);

            var stageChanged = MutateAgentDict(stageAgent, perStageModel, perStageVariant, prevModel, prevVariant);
            if (!stageChanged) continue;

            var stageVarsElement = JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["agent"] = stageAgent },
                WorkflowVariableJson.Options);
            result[stage] = new StageVariables(stageVarsElement);
            touched = true;
        }

        if (!touched) return false;
        newStages = result;
        return true;
    }

    /// <summary>
    /// For a per-stage patch entry, project the value to the named stage's
    /// field state. <c>Clear</c> on the whole map already short-circuits
    /// above, so per-stage mutation only happens when the field is
    /// <c>Set</c> with a dict or <c>Absent</c> (no per-stage entry).
    /// </summary>
    private static FieldPatch<string> ExtractFieldPatch(
        FieldPatch<IReadOnlyDictionary<string, string>> mapPatch,
        string stage)
    {
        if (mapPatch.Kind != FieldPatchKind.Set || mapPatch.Value is null)
            return FieldPatch<string>.Absent;

        if (mapPatch.Value.TryGetValue(stage, out var raw))
        {
            if (raw is null) return FieldPatch<string>.Clear;
            return string.IsNullOrWhiteSpace(raw) ? FieldPatch<string>.Clear : FieldPatch<string>.Set(raw);
        }

        return FieldPatch<string>.Absent;
    }

    /// <summary>
    /// Apply the model + variant patch to the given agent dict in place.
    /// Returns <c>true</c> when the dict was actually mutated.
    /// </summary>
    private static bool MutateAgentDict(
        Dictionary<string, object?> agentVars,
        FieldPatch<string> model,
        FieldPatch<string> modelVariant,
        string? previousModel,
        string? previousVariant)
    {
        if (model.Kind == FieldPatchKind.Set)
        {
            var modelValue = model.Value!;
            agentVars["model"] = modelValue;
            var modelReplaced = !string.Equals(previousModel, modelValue, StringComparison.Ordinal);
            var variantKeyTouched = false;

            if (modelVariant.Kind == FieldPatchKind.Clear)
            {
                if (agentVars.Remove("variant")) variantKeyTouched = true;
            }
            else if (modelVariant.Kind == FieldPatchKind.Set)
            {
                agentVars["variant"] = modelVariant.Value!;
                variantKeyTouched = true;
            }
            else if (modelReplaced && previousVariant is not null)
            {
                // Model changed without an explicit variant patch — drop the
                // stale variant bound to the prior model (dependency invariant).
                if (agentVars.Remove("variant")) variantKeyTouched = true;
            }
            // else: same model re-supplied with no variant patch → preserve.

            return variantKeyTouched || modelReplaced;
        }

        if (model.Kind == FieldPatchKind.Clear)
        {
            // Clear model + bound variant atomically.
            var droppedModel = agentVars.Remove("model");
            var droppedVariant = agentVars.Remove("variant");
            return droppedModel || droppedVariant;
        }

        // model is Absent — handle the variant patch on its own.
        if (modelVariant.Kind == FieldPatchKind.Set)
        {
            // Set variant only — drop if no model is bound (variant without
            // its model is meaningless).
            if (!agentVars.ContainsKey("model")) return false;
            if (string.Equals(previousVariant, modelVariant.Value, StringComparison.Ordinal)) return false;
            agentVars["variant"] = modelVariant.Value!;
            return true;
        }

        if (modelVariant.Kind == FieldPatchKind.Clear)
        {
            // Variant-only clear: drop the variant if (and only if) a model exists.
            if (!agentVars.ContainsKey("model")) return false;
            return agentVars.Remove("variant");
        }

        // Both Absent — nothing to do.
        return false;
    }

    private static HashSet<string> CollectStageNames(
        Dictionary<string, StageVariables>? existing,
        FieldPatch<IReadOnlyDictionary<string, string>> stageModels,
        FieldPatch<IReadOnlyDictionary<string, string>> stageModelVariants)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existing is not null)
        {
            foreach (var key in existing.Keys) names.Add(key);
        }
        if (stageModels.Kind == FieldPatchKind.Set && stageModels.Value is not null)
        {
            foreach (var key in stageModels.Value.Keys) names.Add(key);
        }
        if (stageModelVariants.Kind == FieldPatchKind.Set && stageModelVariants.Value is not null)
        {
            foreach (var key in stageModelVariants.Value.Keys) names.Add(key);
        }
        return names;
    }

    private static Dictionary<string, object?> ExtractAgent(JsonElement? vars)
    {
        if (!vars.HasValue || vars.Value.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        if (vars.Value.TryGetProperty("agent", out var agentEl) && agentEl.ValueKind == JsonValueKind.Object)
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                agentEl.GetRawText(), WorkflowVariableJson.Options)
                ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            return new Dictionary<string, object?>(dict, StringComparer.Ordinal);
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Build the new <c>vars</c> element by SET-replacing the <c>agent</c>
    /// key while preserving every other top-level key from <paramref name="vars"/>.
    /// </summary>
    private static JsonElement? ComposeVars(JsonElement? vars, Dictionary<string, object?> newAgent)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (vars.HasValue && vars.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in vars.Value.EnumerateObject())
            {
                if (string.Equals(prop.Name, "agent", StringComparison.Ordinal)) continue;
                result[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            }
        }
        result["agent"] = newAgent;
        return JsonSerializer.SerializeToElement(result, WorkflowVariableJson.Options);
    }

    private static string? AsString(object? raw) => raw switch
    {
        null => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s,
        JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
        _ => null,
    };
}
