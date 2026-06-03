using System.Text.Json;

namespace Mohist.Server.Workflow.Infrastructure;

/// <summary>
/// 解析后的模板: 选定生效模板的最终结构。
/// 
/// 选择优先级 (高 -> 低):
///   1. workflow_profile.Template (run 快照)
///   2. issue_workflow_profile.Template (issue 自定义)
///   3. project_templates 中 SourceTemplateId 引用的模板
///   4. project_workflow_profile.DefaultTemplateId 引用的项目默认模板
/// </summary>
public sealed record ResolvedTemplate(
    string? Id,
    Mohist.Server.Workflow.Domain.Definition.WorkflowDefinition? Structure,
    Mohist.Server.Workflow.Domain.VariableBundle? EmbeddedVariables)
{
    public static readonly ResolvedTemplate None = new(null, null, null);

    /// <summary>
    /// 从 WorkflowDefinition 创建 ResolvedTemplate, 自动提取 Variables 段作为 embeddedVariables。
    /// </summary>
    public static ResolvedTemplate FromDefinition(
        string? id,
        Mohist.Server.Workflow.Domain.Definition.WorkflowDefinition? definition)
    {
        if (definition is null) return None;

        var embedded = ExtractEmbeddedVariables(definition);
        return new ResolvedTemplate(id, definition, embedded);
    }

    private static Mohist.Server.Workflow.Domain.VariableBundle? ExtractEmbeddedVariables(
        Mohist.Server.Workflow.Domain.Definition.WorkflowDefinition definition)
    {
        var globalVars = definition.Variables;
        var stageVars = new Dictionary<string, Mohist.Server.Workflow.Domain.StageVariables>(
            StringComparer.OrdinalIgnoreCase);

        if (definition.Stages is not null)
        {
            foreach (var stage in definition.Stages)
            {
                if (stage is null || stage.Variables is null || stage.Variables.Count == 0) continue;

                var stageVarsObj = BuildStageVarsElement(stage.Variables);
                stageVars[stage.Stage] = new Mohist.Server.Workflow.Domain.StageVariables(stageVarsObj);
            }
        }

        if (globalVars is null && stageVars.Count == 0)
            return null;

        var globalElement = globalVars is not null
            ? JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(globalVars))
            : (JsonElement?)null;

        return new Mohist.Server.Workflow.Domain.VariableBundle(
            globalElement,
            stageVars.Count > 0 ? stageVars : null);
    }

    private static JsonElement BuildStageVarsElement(Dictionary<string, JsonElement?> stageVariables)
    {
        var dict = stageVariables
            .Where(kv => kv.Value.HasValue)
            .ToDictionary(kv => kv.Key, kv => kv.Value!.Value);

        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dict));
    }
}
