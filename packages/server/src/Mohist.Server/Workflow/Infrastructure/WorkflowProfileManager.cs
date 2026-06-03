using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Workflow.Infrastructure;

/// <summary>
/// Workflow 数据唯一读入口。
/// 
/// LoadTemplate: 选定生效模板 (snapshot > issue custom > issue-ref-template > project default)
/// LoadVariables: 合并 3 层独立变量 (project + issue + workflow-run)
/// ExpandTaskWith: 展开 task.with 中的 ${{ }} 模板表达式
/// </summary>
public class WorkflowProfileManager
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowProfileManager(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// 选定生效模板。
    /// 优先级: workflow_profile.Template > issue_workflow_profile.Template (custom) >
    ///         project_templates[issue.SourceTemplateId] > project_templates[project.DefaultTemplateId]
    /// </summary>
    public async Task<ResolvedTemplate> LoadTemplateAsync(string runId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var runProfile = await db.WorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == runId);

        // Priority 1: run 快照
        if (runProfile is not null && !string.IsNullOrWhiteSpace(runProfile.TemplateJson))
        {
            var runDef = DeserializeDefinition(runProfile.TemplateJson);
            if (runDef is not null && !string.IsNullOrWhiteSpace(runDef.Id))
                return ResolvedTemplate.FromDefinition($"run-snapshot:{runId}", runDef);
        }

        if (runProfile is null)
            return ResolvedTemplate.None;

        var issueProfile = await db.IssueWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IssueKey == runProfile.IssueKey);

        // Priority 2: issue 自定义模板 (TemplateJson 不为 null)
        if (issueProfile is not null && !string.IsNullOrWhiteSpace(issueProfile.TemplateJson))
        {
            var issueDef = DeserializeDefinition(issueProfile.TemplateJson);
            if (issueDef is not null && !string.IsNullOrWhiteSpace(issueDef.Id))
                return ResolvedTemplate.FromDefinition($"issue-custom:{runProfile.IssueKey}", issueDef);
        }

        // Priority 3: issue 引用的项目模板
        if (issueProfile is not null && !string.IsNullOrWhiteSpace(issueProfile.SourceTemplateId))
        {
            var template = await LoadProjectTemplateAsync(db, runProfile.ProjectId, issueProfile.SourceTemplateId);
            if (template is not null)
                return template;
        }

        // Priority 4: 项目默认模板
        var projectProfile = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == runProfile.ProjectId);
        if (projectProfile is not null && !string.IsNullOrWhiteSpace(projectProfile.DefaultTemplateId))
        {
            var template = await LoadProjectTemplateAsync(db, runProfile.ProjectId, projectProfile.DefaultTemplateId);
            if (template is not null)
                return template;
        }

        return ResolvedTemplate.None;
    }

    /// <summary>
    /// 合并 3 层独立变量 (project + issue + workflow-run)。
    /// 优先级: workflow-run > issue > project (深合并, 后者覆盖前者)。
    /// </summary>
    public async Task<VariableBundle> LoadVariablesAsync(string runId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var runProfile = await db.WorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == runId);
        if (runProfile is null)
            return VariableBundle.Empty;

        var projectProfile = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == runProfile.ProjectId);
        var issueProfile = await db.IssueWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IssueKey == runProfile.IssueKey);

        var projectBundle = VariableBundle.FromJson(projectProfile?.VariablesJson);
        var issueBundle = VariableBundle.FromJson(issueProfile?.VariablesJson);
        var runBundle = VariableBundle.FromJson(runProfile.VariablesJson);

        return VariableBundle.MergeAll(projectBundle, issueBundle, runBundle);
    }

    /// <summary>
    /// 展开 task.with 中的模板表达式。
    /// 
    /// 规则:
    ///   - value 是 "${{ path }}" 字符串 → 从 resolved 取值替换
    ///   - value 是 JSON 对象 且 resolved.vars 中有同名 key → deep merge (vars 覆盖)
    ///   - 其他 → 保留原值
    /// </summary>
    public static Dictionary<string, JsonElement?>? ExpandTaskWith(
        VariableBundle? resolved,
        Dictionary<string, JsonElement?>? taskWith)
    {
        if (taskWith is null || taskWith.Count == 0) return taskWith;
        if (resolved?.Vars is null) return taskWith;

        var result = new Dictionary<string, JsonElement?>(taskWith.Count, StringComparer.Ordinal);
        using var varsDoc = JsonDocument.Parse(resolved.Vars.Value.GetRawText());
        var varsRoot = varsDoc.RootElement;

        foreach (var (key, value) in taskWith)
        {
            if (!value.HasValue)
            {
                result[key] = value;
                continue;
            }

            var v = value.Value;

            // 模板字符串 "${{ ... }}"
            if (v.ValueKind == JsonValueKind.String)
            {
                var template = v.GetString();
                if (IsTemplateExpression(template, out var path))
                {
                    var expanded = ResolvePath(varsRoot, path);
                    result[key] = expanded.HasValue ? expanded.Value.Clone() : (JsonElement?)null;
                    continue;
                }
                result[key] = v.Clone();
                continue;
            }

            // 对象 且 vars 中有同名 key → deep merge
            if (v.ValueKind == JsonValueKind.Object
                && varsRoot.ValueKind == JsonValueKind.Object
                && varsRoot.TryGetProperty(key, out var varsOverride)
                && varsOverride.ValueKind == JsonValueKind.Object)
            {
                var merged = VariableBundle.DeepMerge(v, varsOverride);
                result[key] = merged;
                continue;
            }

            // 其他: 保留原值
            result[key] = v.Clone();
        }

        return result;
    }

    private static bool IsTemplateExpression(string? value, out string path)
    {
        if (!string.IsNullOrEmpty(value)
            && value.StartsWith("${{", StringComparison.Ordinal)
            && value.EndsWith("}}", StringComparison.Ordinal)
            && value.Trim().Length == value.Length) // no whitespace around edges
        {
            path = value[3..^2].Trim();
            return true;
        }
        path = string.Empty;
        return false;
    }

    private static JsonElement? ResolvePath(JsonElement root, string path)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        foreach (var part in parts)
        {
            if (current.ValueKind != JsonValueKind.Object)
                return null;
            if (!current.TryGetProperty(part, out var next))
                return null;
            current = next;
        }
        return current;
    }

    private static async Task<ResolvedTemplate?> LoadProjectTemplateAsync(
        MohistDbContext db, string projectId, string templateId)
    {
        var row = await db.ProjectTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TemplateId == templateId);
        if (row is null) return null;

        var def = DeserializeDefinition(row.TemplateJson);
        return def is null ? null : ResolvedTemplate.FromDefinition($"project-template:{projectId}/{templateId}", def);
    }

    private static WorkflowDefinition? DeserializeDefinition(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<WorkflowDefinition>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                });
        }
        catch
        {
            return null;
        }
    }
}
