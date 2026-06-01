using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.WorkflowProfiles;

public class IssueWorkflowProfile
{
    public string SourceProfileId { get; private set; }
    public WorkflowDefinition Definition { get; private set; }
    public WorkflowProfileUpdateMode UpdateMode { get; private set; }

    public IssueWorkflowProfile(string sourceProfileId, WorkflowDefinition definition,
        WorkflowProfileUpdateMode updateMode = WorkflowProfileUpdateMode.Reference)
    {
        SourceProfileId = sourceProfileId;
        Definition = definition;
        UpdateMode = updateMode;
    }

    public static IssueWorkflowProfile CopyFrom(
        string sourceProfileId,
        WorkflowDefinition template,
        Dictionary<string, object?>? globalAgentConfig = null,
        Dictionary<string, Dictionary<string, object?>>? globalStageAgentConfigs = null)
    {
        var definition = DeepCopy(template);
        definition = MergeAgentIntoVariables(definition, globalAgentConfig);
        definition = MergeStageAgentIntoVariables(definition, globalStageAgentConfigs);
        return new IssueWorkflowProfile(sourceProfileId, definition);
    }

    public void ApplyCustomDefinition(string sourceProfileId, WorkflowDefinition definition)
    {
        SourceProfileId = sourceProfileId;
        Definition = DeepCopy(definition);
        UpdateMode = WorkflowProfileUpdateMode.Custom;
    }

    public void SwitchTo(
        string sourceProfileId,
        WorkflowDefinition template,
        Dictionary<string, object?>? globalAgentConfig = null,
        Dictionary<string, Dictionary<string, object?>>? globalStageAgentConfigs = null)
    {
        SourceProfileId = sourceProfileId;
        Definition = DeepCopy(template);
        Definition = MergeAgentIntoVariables(Definition, globalAgentConfig);
        Definition = MergeStageAgentIntoVariables(Definition, globalStageAgentConfigs);
        UpdateMode = WorkflowProfileUpdateMode.Reference;
    }

    public void PatchVariables(string path, object? value)
    {
        var variables = Definition.Variables != null
            ? new Dictionary<string, JsonElement?>(Definition.Variables, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        PatchNested(variables, path.Split('.'), 0, value);
        Definition = Definition with { Variables = variables };
    }

    public void PatchStageVariables(string stageName, string path, object? value)
    {
        var stages = Definition.Stages.Select(stage =>
        {
            if (!string.Equals(stage.Stage, stageName, StringComparison.OrdinalIgnoreCase)) return stage;
            var stageVars = stage.Variables != null
                ? new Dictionary<string, JsonElement?>(stage.Variables, StringComparer.Ordinal)
                : new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
            PatchNested(stageVars, path.Split('.'), 0, value);
            return stage with { Variables = stageVars };
        }).ToList();
        Definition = Definition with { Stages = stages };
    }

    private static void PatchNested(Dictionary<string, JsonElement?> target, string[] parts, int index, object? value)
    {
        if (index == parts.Length - 1)
        {
            target[parts[index]] = value is null ? null : JsonSerializer.SerializeToElement(value, WorkflowVariableJson.Options);
            return;
        }

        var key = parts[index];
        Dictionary<string, JsonElement?> dict;
        if (target.TryGetValue(key, out var existing) && existing.HasValue && existing.Value.ValueKind == JsonValueKind.Object)
        {
            dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(existing.Value.GetRawText(), WorkflowVariableJson.Options)!
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }
        else
        {
            dict = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        }

        PatchNested(dict, parts, index + 1, value);
        target[key] = JsonSerializer.SerializeToElement(dict, WorkflowVariableJson.Options);
    }

    private static WorkflowDefinition MergeAgentIntoVariables(WorkflowDefinition definition, Dictionary<string, object?>? agentConfig)
    {
        if (agentConfig is null || agentConfig.Count == 0) return definition;

        var variables = definition.Variables != null
            ? new Dictionary<string, JsonElement?>(definition.Variables, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement?>(StringComparer.Ordinal);

        Dictionary<string, object?> merged;
        if (variables.TryGetValue("agent", out var existingAgent) && existingAgent.HasValue && existingAgent.Value.ValueKind == JsonValueKind.Object)
        {
            merged = JsonSerializer.Deserialize<Dictionary<string, object?>>(existingAgent.Value.GetRawText(), WorkflowVariableJson.Options)!;

        }
        else
        {
            merged = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "opencode" };
        }

        foreach (var (key, val) in agentConfig)
            if (val is not null) merged[key] = val;
        if (!merged.ContainsKey("type")) merged["type"] = "opencode";
        variables["agent"] = JsonSerializer.SerializeToElement(merged, WorkflowVariableJson.Options);

        return definition with { Variables = variables };
    }

    private static WorkflowDefinition MergeStageAgentIntoVariables(WorkflowDefinition definition, Dictionary<string, Dictionary<string, object?>>? stageConfigs)
    {
        if (stageConfigs is null || stageConfigs.Count == 0) return definition;

        var stages = definition.Stages.Select(stage =>
        {
            var stageConfig = stageConfigs.FirstOrDefault(kv => string.Equals(kv.Key, stage.Stage, StringComparison.OrdinalIgnoreCase));
            if (stageConfig.Key is null || stageConfig.Value is null || stageConfig.Value.Count == 0) return stage;

            var stageVars = stage.Variables != null
                ? new Dictionary<string, JsonElement?>(stage.Variables, StringComparer.Ordinal)
                : new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
            stageVars["agent"] = JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>(stageConfig.Value, StringComparer.Ordinal),
                WorkflowVariableJson.Options);

            return stage with { Variables = stageVars };
        }).ToList();

        return definition with { Stages = stages };
    }

    private static WorkflowDefinition DeepCopy(WorkflowDefinition source)
    {
        var json = JsonSerializer.Serialize(source, WorkflowVariableJson.Options);
        return JsonSerializer.Deserialize<WorkflowDefinition>(json, WorkflowVariableJson.Options)!;
    }
}
