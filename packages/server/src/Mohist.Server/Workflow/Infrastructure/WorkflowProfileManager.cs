using System.Text.Json;
using System.Text.Json.Serialization;
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
/// ExpandTaskWith: 处理 task.with (保留 ${{ }} 给 runner 展开, 对象 deep merge)
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
    /// 
    /// 重要: 此方法只合并 project + issue 两层; workflow-run 层 (legacy StageVariables + 运行时
    /// 补丁) 由 WorkflowGrain.MakeDispatchAsync 按以下 legacy 顺序单独应用:
    ///   1. _variables.Json["vars"] (模板初始值)
    ///   2. _variables.StageVariables[stage] (workflow 阶段补丁, 较早)
    ///   3. combinedVars.Vars (project+issue 全局)
    ///   4. combinedVars.Stages[stage] (project+issue 阶段)
    ///   5. dispatch scope 注入
    /// 
    /// 这样保证 legacy 顺序: project stage > workflow-run stage。
    /// 
    /// 兼容: 当新表 (project_workflow_profile.Variables / issue_workflow_profile.Variables) 为空时,
    /// 回退读取旧表 (projects.VariablesJson / issue_profiles.StateJson 等)。
    /// </summary>
    /// <param name="runId">WorkflowRunId (必传)</param>
    /// <param name="projectId">ProjectId (可选; 用于回退读取旧表)</param>
    /// <param name="issueKey">IssueKey (可选; 用于回退读取旧表, 格式 projectId:issueNumber)</param>
    public async Task<VariableBundle> LoadVariablesAsync(string runId, string? projectId = null, string? issueKey = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var runProfile = await db.WorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == runId);

        // If new-table row missing, try to discover projectId/issueKey from legacy tables
        if (runProfile is not null)
        {
            projectId ??= runProfile.ProjectId;
            issueKey ??= runProfile.IssueKey;
        }
        else if (string.IsNullOrEmpty(projectId))
        {
            // Last resort: look up projectId from workflow_runs.MetadataProjectId
            var legacyRun = await db.WorkflowRuns.AsNoTracking()
                .FirstOrDefaultAsync(x => x.WorkflowRunId == runId);
            projectId = legacyRun?.MetadataProjectId;
        }

        var projectProfile = projectId is not null
            ? await db.ProjectWorkflowProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId)
            : null;
        var issueProfile = issueKey is not null
            ? await db.IssueWorkflowProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.IssueKey == issueKey)
            : null;

        // Project layer: new table → fallback to old projects.VariablesJson
        var projectBundle = VariableBundle.FromJson(projectProfile?.VariablesJson);
        if (projectBundle.Stages is null && !projectBundle.Vars.HasValue && projectId is not null)
        {
            var legacyBag = await LoadLegacyProjectVariablesAsync(db, projectId);
            if (legacyBag is not null)
                projectBundle = ConvertProjectVariablesBag(legacyBag);
        }

        var issueBundle = VariableBundle.FromJson(issueProfile?.VariablesJson);

        // Do NOT merge runBundle here — WorkflowGrain.MakeDispatchAsync applies legacy
        // _variables.StageVariables BEFORE combinedVars so project stage wins over
        // workflow stage (matching legacy ToDispatchJson order).
        if ((projectBundle.Stages is null || projectBundle.Stages.Count == 0) && !projectBundle.Vars.HasValue
            && (issueBundle.Stages is null || issueBundle.Stages.Count == 0) && !issueBundle.Vars.HasValue)
        {
            return VariableBundle.Empty;
        }

        return VariableBundle.MergeAll(projectBundle, issueBundle);
    }

    /// <summary>
    /// 处理 task.with: 
    ///   - value 是 JSON 对象 且 effectiveVars 中有同名 key → deep merge (vars 覆盖)
    ///   - 其他 (包括 ${{ }} 模板字符串) → 保留原值, 由 runner 端展开
    /// </summary>
    public static Dictionary<string, JsonElement?>? ExpandTaskWith(
        VariableBundle? effectiveVars,
        Dictionary<string, JsonElement?>? taskWith)
    {
        if (taskWith is null || taskWith.Count == 0) return taskWith;
        if (effectiveVars?.Vars is null || effectiveVars.Vars.Value.ValueKind != JsonValueKind.Object) return taskWith;

        using var varsDoc = JsonDocument.Parse(effectiveVars.Vars.Value.GetRawText());
        var varsRoot = varsDoc.RootElement;

        var result = new Dictionary<string, JsonElement?>(taskWith.Count, StringComparer.Ordinal);

        foreach (var (key, value) in taskWith)
        {
            if (!value.HasValue)
            {
                result[key] = value;
                continue;
            }

            var v = value.Value;

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

            // 其他 (包括 ${{ }} 模板字符串): 保留原值, 由 runner 端展开
            result[key] = v.Clone();
        }

        return result;
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

    // =======================================================================
    // Legacy fallbacks (Step 7 compatibility)
    // =======================================================================

    private static readonly JsonSerializerOptions LegacyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static async Task<LegacyProjectVars?> LoadLegacyProjectVariablesAsync(
        MohistDbContext db, string projectId)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT VariablesJson FROM Projects WHERE Id = @id";

        var param = cmd.CreateParameter();
        param.ParameterName = "@id";
        param.Value = projectId;
        cmd.Parameters.Add(param);

        var result = await cmd.ExecuteScalarAsync();
        if (result is not string json || string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<LegacyProjectVars>(json, LegacyJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<VariableBundle?> LoadLegacyRunVariablesAsync(
        MohistDbContext db, string runId)
    {
        var row = await db.WorkflowVariables.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == runId);
        if (row is null) return null;

        try
        {
            // Legacy WorkflowExecutionContext: { Json: string (inner JSON), StageVariables: object }
            // StageVariables layout: { stageName: { sectionName: jsonString } }
            using var doc = JsonDocument.Parse(row.StateJson ?? "{}");
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            JsonElement? varsEl = null;
            if (doc.RootElement.TryGetProperty("Json", out var jsonEl)
                && jsonEl.ValueKind == JsonValueKind.String)
            {
                var innerJson = jsonEl.GetString();
                if (!string.IsNullOrWhiteSpace(innerJson))
                {
                    using var innerDoc = JsonDocument.Parse(innerJson);
                    if (innerDoc.RootElement.ValueKind == JsonValueKind.Object
                        && innerDoc.RootElement.TryGetProperty("vars", out var innerVars)
                        && innerVars.ValueKind == JsonValueKind.Object)
                    {
                        varsEl = innerVars.Clone();
                    }
                }
            }

            Dictionary<string, StageVariables>? stages = null;
            if (doc.RootElement.TryGetProperty("StageVariables", out var svEl)
                && svEl.ValueKind == JsonValueKind.Object)
            {
                stages = new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase);
                foreach (var stageProp in svEl.EnumerateObject())
                {
                    if (stageProp.Value.ValueKind != JsonValueKind.Object) continue;

                    // Each stage value is a Dict<sectionName, jsonString>
                    foreach (var sectionProp in stageProp.Value.EnumerateObject())
                    {
                        // Only collect "vars" sections into our StageVariables
                        if (!string.Equals(sectionProp.Name, "vars", StringComparison.Ordinal))
                            continue;
                        if (sectionProp.Value.ValueKind != JsonValueKind.String)
                            continue;
                        var sectionJson = sectionProp.Value.GetString();
                        if (string.IsNullOrWhiteSpace(sectionJson))
                            continue;
                        var sectionElement = JsonSerializer.Deserialize<JsonElement>(sectionJson);
                        if (sectionElement.ValueKind == JsonValueKind.Object)
                            stages[stageProp.Name] = new StageVariables(sectionElement);
                    }
                }
                if (stages.Count == 0) stages = null;
            }

            if (!varsEl.HasValue && stages is null) return null;
            return new VariableBundle(varsEl, stages);
        }
        catch
        {
            return null;
        }
    }

    private static VariableBundle ConvertProjectVariablesBag(LegacyProjectVars bag)
    {
        JsonElement? varsEl = null;
        if (bag.Vars is { Count: > 0 })
        {
            varsEl = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(bag.Vars, LegacyJsonOptions));
        }

        Dictionary<string, StageVariables>? stages = null;
        if (bag.Stages is { Count: > 0 })
        {
            stages = new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase);
            foreach (var (stage, sbag) in bag.Stages)
            {
                if (sbag is null || sbag.Vars is null || sbag.Vars.Count == 0) continue;
                var stageEl = JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(sbag.Vars, LegacyJsonOptions));
                stages[stage] = new StageVariables(stageEl);
            }
            if (stages.Count == 0) stages = null;
        }

        return new VariableBundle(varsEl, stages);
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

    private sealed record LegacyProjectVars(
        Dictionary<string, JsonElement?>? Vars = null,
        Dictionary<string, LegacyProjectStageVars?>? Stages = null);

    private sealed record LegacyProjectStageVars(
        Dictionary<string, JsonElement?>? Vars = null);
}
