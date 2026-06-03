using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Workflow.Infrastructure;

/// <summary>
/// Workflow 数据唯一读写入口。
///
/// SaveProfile: 写入 workflow run-level 的 ProfileRow (模板快照 + ProjectId/IssueKey)
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
    /// 创建或更新 workflow run 级别的 ProfileRow (模板快照 + ProjectId/IssueKey)。
    /// Workflow 启动时调用, 将模板冻结, 并记录关联的 project/issue 供后续 LoadVariables 使用。
    /// </summary>
    public async Task<WorkflowProfileRow> SaveProfileAsync(
        string runId,
        WorkflowDefinition definition,
        string? projectId,
        string? issueKey)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.WorkflowProfiles.FirstOrDefaultAsync(x => x.WorkflowRunId == runId);
        var templateJson = JsonSerializer.Serialize(definition, WorkflowYamlSerializer.JsonOptions);

        if (existing is null)
        {
            existing = new WorkflowProfileRow
            {
                WorkflowRunId = runId,
                ProjectId = projectId ?? "",
                IssueKey = issueKey ?? "",
                TemplateJson = templateJson,
                VariablesJson = VariableBundle.Empty.ToJson(),
            };
            db.WorkflowProfiles.Add(existing);
        }
        else
        {
            existing.ProjectId = projectId ?? existing.ProjectId;
            existing.IssueKey = issueKey ?? existing.IssueKey;
            existing.TemplateJson = templateJson;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return existing;
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
    /// 对齐 design/workflow-template-variables.md 设计:
    ///   project_workflow_profile.Variables  ─┐
    ///   issue_workflow_profile.Variables   ─┤  deepMerge  ─→ independent
    ///   workflow_profile.Variables         ─┘
    /// 
    /// projectId / issueKey 从 WorkflowProfileRow 自动发现 (由 StartAsync.SaveProfileAsync 写入)。
    /// 兼容: 新表为空时回退读取旧表 (projects.VariablesJson / workflow_variables.StateJson 等)。
    /// </summary>
    /// <param name="runId">WorkflowRunId (必传)</param>
    public async Task<VariableBundle> LoadVariablesAsync(string runId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var runProfile = await db.WorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == runId);

        string? projectId = runProfile?.ProjectId;
        string? issueKey = runProfile?.IssueKey;

        if (string.IsNullOrEmpty(projectId))
        {
            var legacyRun = await db.WorkflowRuns.AsNoTracking()
                .FirstOrDefaultAsync(x => x.WorkflowRunId == runId);
            projectId = legacyRun?.MetadataProjectId;
        }

        var projectBundle = await LoadProjectLayerAsync(db, projectId);
        var issueBundle = await LoadIssueLayerAsync(db, issueKey);
        var runBundle = await LoadRunLayerAsync(db, runProfile, runId);

        if (!projectBundle.Vars.HasValue
            && (projectBundle.Stages is null || projectBundle.Stages.Count == 0)
            && !issueBundle.Vars.HasValue
            && (issueBundle.Stages is null || issueBundle.Stages.Count == 0)
            && !runBundle.Vars.HasValue
            && (runBundle.Stages is null || runBundle.Stages.Count == 0))
        {
            return VariableBundle.Empty;
        }

        return VariableBundle.MergeAll(projectBundle, issueBundle, runBundle);
    }

    private async Task<VariableBundle> LoadProjectLayerAsync(MohistDbContext db, string? projectId)
    {
        if (projectId is null) return VariableBundle.Empty;
        var projectProfile = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);
        var bundle = VariableBundle.FromJson(projectProfile?.VariablesJson);
        if (!bundle.Vars.HasValue && bundle.Stages is null)
        {
            var legacyBag = await LoadLegacyProjectVariablesAsync(db, projectId);
            if (legacyBag is not null)
                bundle = ConvertProjectVariablesBag(legacyBag);
        }
        return bundle;
    }

    private static async Task<VariableBundle> LoadIssueLayerAsync(MohistDbContext db, string? issueKey)
    {
        if (issueKey is null) return VariableBundle.Empty;
        var issueProfile = await db.IssueWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IssueKey == issueKey);
        return VariableBundle.FromJson(issueProfile?.VariablesJson);
    }

    private static async Task<VariableBundle> LoadRunLayerAsync(
        MohistDbContext db, WorkflowProfileRow? runProfile, string runId)
    {
        var runBundle = VariableBundle.FromJson(runProfile?.VariablesJson);
        if (!runBundle.Vars.HasValue && runBundle.Stages is null)
        {
            var legacyBundle = await LoadLegacyRunVariablesAsync(db, runId);
            if (legacyBundle is not null)
                return legacyBundle;
        }
        return runBundle;
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

    /// <summary>
    /// 补丁 workflow-run 层变量 (section 粒度, e.g. "agent", "vars")。
    /// PatchVariablesAsync(section, patchJson): deep merge patchJson 进 bundle.Vars[section]。
    /// 仅写 workflow_profile.VariablesJson; legacy 路径由 WorkflowGrain 另行维持。
    /// </summary>
    public async Task<VariableBundle> PatchRunVariablesAsync(string runId, string section, string patchJson)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowProfiles.FirstOrDefaultAsync(x => x.WorkflowRunId == runId);
        row ??= EnsureRunProfileRow(db, runId);

        var current = VariableBundle.FromJson(row.VariablesJson);
        var patchSectionEl = JsonSerializer.Deserialize<JsonElement>(patchJson);
        var patchedBundle = PatchBundleSection(current, section, patchSectionEl);

        row.VariablesJson = patchedBundle.ToJson();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return patchedBundle;
    }

    /// <summary>
    /// 补丁 workflow-run 层阶段变量 (stage + section 粒度)。
    /// deep merge patchJson 进 bundle.Stages[stage].Vars[section]。
    /// </summary>
    public async Task<VariableBundle> PatchRunStageVariablesAsync(
        string runId, string stage, string section, string patchJson)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowProfiles.FirstOrDefaultAsync(x => x.WorkflowRunId == runId);
        row ??= EnsureRunProfileRow(db, runId);

        var current = VariableBundle.FromJson(row.VariablesJson);
        var patchSectionEl = JsonSerializer.Deserialize<JsonElement>(patchJson);
        var patchedBundle = PatchBundleStageSection(current, stage, section, patchSectionEl);

        row.VariablesJson = patchedBundle.ToJson();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return patchedBundle;
    }

    private static WorkflowProfileRow EnsureRunProfileRow(MohistDbContext db, string runId)
    {
        var row = new WorkflowProfileRow
        {
            WorkflowRunId = runId,
            ProjectId = "",
            IssueKey = "",
            TemplateJson = "{}",
            VariablesJson = VariableBundle.Empty.ToJson(),
        };
        db.WorkflowProfiles.Add(row);
        return row;
    }

    private static VariableBundle PatchBundleSection(VariableBundle bundle, string section, JsonElement patch)
    {
        if (section == "vars")
        {
            var current = bundle.Vars.HasValue && bundle.Vars.Value.ValueKind == JsonValueKind.Object
                ? bundle.Vars.Value
                : JsonSerializer.Deserialize<JsonElement>("{}");
            var merged = patch.ValueKind == JsonValueKind.Object
                ? VariableBundle.DeepMerge(current, patch) ?? patch
                : patch;
            return bundle with { Vars = merged };
        }

        JsonElement? mergedSection;
        if (bundle.Vars.HasValue && bundle.Vars.Value.ValueKind == JsonValueKind.Object
            && bundle.Vars.Value.TryGetProperty(section, out var existing)
            && existing.ValueKind == JsonValueKind.Object
            && patch.ValueKind == JsonValueKind.Object)
        {
            mergedSection = VariableBundle.DeepMerge(existing, patch) ?? patch;
        }
        else
        {
            mergedSection = patch;
        }

        var varsRoot = bundle.Vars.HasValue && bundle.Vars.Value.ValueKind == JsonValueKind.Object
            ? bundle.Vars.Value
            : JsonSerializer.Deserialize<JsonElement>("{}");

        using var doc = JsonDocument.Parse(varsRoot.GetRawText());
        var newVarsDict = new Dictionary<string, JsonElement?>(doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => (JsonElement?)p.Value.Clone()));
        newVarsDict[section] = mergedSection!.Value;

        var serialized = JsonSerializer.Serialize(newVarsDict, WorkflowYamlSerializer.JsonOptions);
        var newVars = JsonSerializer.Deserialize<JsonElement>(serialized);
        return bundle with { Vars = newVars };
    }

    private static VariableBundle PatchBundleStageSection(
        VariableBundle bundle, string stage, string section, JsonElement patch)
    {
        var stages = bundle.Stages is null
            ? new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, StageVariables>(bundle.Stages, StringComparer.OrdinalIgnoreCase);

        var stageVars = stages.TryGetValue(stage, out var existing) ? existing : new StageVariables(JsonSerializer.Deserialize<JsonElement>("{}"));
        var stageEl = stageVars.Vars.HasValue && stageVars.Vars.Value.ValueKind == JsonValueKind.Object
            ? stageVars.Vars.Value
            : JsonSerializer.Deserialize<JsonElement>("{}");

        if (section == "vars")
        {
            var mergedStageVars = patch.ValueKind == JsonValueKind.Object
                ? VariableBundle.DeepMerge(stageEl, patch) ?? patch
                : patch;
            stages[stage] = new StageVariables(mergedStageVars);
            return bundle with { Stages = stages };
        }

        JsonElement? mergedSection;
        if (stageEl.TryGetProperty(section, out var secExisting)
            && secExisting.ValueKind == JsonValueKind.Object
            && patch.ValueKind == JsonValueKind.Object)
        {
            mergedSection = VariableBundle.DeepMerge(secExisting, patch) ?? patch;
        }
        else
        {
            mergedSection = patch;
        }

        using var doc = JsonDocument.Parse(stageEl.GetRawText());
        var newStageDict = new Dictionary<string, JsonElement?>(doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => (JsonElement?)p.Value.Clone()));
        newStageDict[section] = mergedSection!.Value;

        var newStageSerialized = JsonSerializer.Serialize(newStageDict, WorkflowYamlSerializer.JsonOptions);
        var newStageEl = JsonSerializer.Deserialize<JsonElement>(newStageSerialized);
        stages[stage] = new StageVariables(newStageEl);
        return bundle with { Stages = stages };
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
