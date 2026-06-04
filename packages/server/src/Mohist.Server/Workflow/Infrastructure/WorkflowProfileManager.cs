using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Prompts;
using Mohist.Server.Workflow.Prompts.Infrastructure;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Workflow.Infrastructure;

/// <summary>
/// Workflow template/variables/prompts resolution entrypoint.
/// Template resolution never depends on a workflow-run profile snapshot:
/// issue custom > issue referenced template > project default > system default.
/// </summary>
public class WorkflowProfileManager
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IPromptLoader _promptLoader;
    private readonly PromptTemplateEngine _engine;

    public WorkflowProfileManager(
        IDbContextFactory<MohistDbContext> dbFactory,
        IPromptLoader promptLoader,
        PromptTemplateEngine engine)
    {
        _dbFactory = dbFactory;
        _promptLoader = promptLoader;
        _engine = engine;
    }

    public async Task<ResolvedTemplate> LoadTemplateAsync(
        string runId,
        string? projectId = null,
        string? issueId = null,
        string? legacyIssueKey = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var resolvedContext = await ResolveRunContextAsync(db, runId);
        var context = new RunContext(
            string.IsNullOrWhiteSpace(projectId) ? resolvedContext.ProjectId : projectId,
            string.IsNullOrWhiteSpace(issueId) ? resolvedContext.IssueId : issueId,
            string.IsNullOrWhiteSpace(legacyIssueKey) ? resolvedContext.LegacyIssueKey : legacyIssueKey);

        var issueProfile = await LoadIssueProfileAsync(db, context);

        if (issueProfile is not null && !string.IsNullOrWhiteSpace(issueProfile.TemplateJson))
        {
            var issueDef = DeserializeDefinition(issueProfile.TemplateJson);
            if (issueDef is not null && !string.IsNullOrWhiteSpace(issueDef.Id))
                return ResolvedTemplate.FromDefinition($"issue-custom:{issueProfile.IssueKey}", issueDef);
        }

        if (issueProfile is not null
            && !string.IsNullOrWhiteSpace(issueProfile.SourceTemplateId)
            && !string.IsNullOrWhiteSpace(context.ProjectId))
        {
            var template = await LoadTemplateReferenceAsync(db, context.ProjectId, issueProfile.SourceTemplateId);
            if (template is not null)
                return template;
        }

        if (!string.IsNullOrWhiteSpace(context.ProjectId))
        {
            var projectProfile = await db.ProjectWorkflowProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProjectId == context.ProjectId);
            if (projectProfile is not null && !string.IsNullOrWhiteSpace(projectProfile.DefaultTemplateId))
            {
                var template = await LoadTemplateReferenceAsync(db, context.ProjectId, projectProfile.DefaultTemplateId);
                if (template is not null)
                    return template;
            }
        }

        return ResolvedTemplate.FromDefinition(
            $"system-template:{IssueWorkflowProfiles.DefaultId}",
            ProjectWorkflowProfileManager.GetSystemTemplateDefinition(IssueWorkflowProfiles.DefaultId));
    }

    public async Task<VariableBundle> LoadVariablesAsync(string runId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var context = await ResolveRunContextAsync(db, runId);

        var projectBundle = await LoadProjectLayerAsync(db, context.ProjectId);
        var issueBundle = await LoadIssueLayerAsync(db, context);

        if (!projectBundle.Vars.HasValue
            && (projectBundle.Stages is null || projectBundle.Stages.Count == 0)
            && !issueBundle.Vars.HasValue
            && (issueBundle.Stages is null || issueBundle.Stages.Count == 0))
        {
            return VariableBundle.Empty;
        }

        return VariableBundle.MergeAll(projectBundle, issueBundle);
    }

    private static async Task<RunContext> ResolveRunContextAsync(MohistDbContext db, string runId)
    {
        var workflowRun = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == runId);

        var projectId = workflowRun?.MetadataProjectId;
        var issueId = TryReadAnnotation(workflowRun?.State, "issueId");
        var issueKey = TryReadAnnotation(workflowRun?.State, "issueKey");
        var issue = await FindIssueForRunAsync(db, runId);
        projectId = string.IsNullOrWhiteSpace(projectId) ? issue?.ProjectId : projectId;
        issueId = string.IsNullOrWhiteSpace(issueId) ? issue?.IssueId : issueId;
        issueKey = string.IsNullOrWhiteSpace(issueKey) && issue is not null
            ? $"{issue.ProjectId}:{issue.Number}"
            : issueKey;

        return new RunContext(projectId, issueId, issueKey);
    }

    private static string? TryReadAnnotation(string? stateJson, string key)
    {
        if (string.IsNullOrWhiteSpace(stateJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(stateJson);
            if (!doc.RootElement.TryGetProperty("Metadata", out var metadata)
                || metadata.ValueKind != JsonValueKind.Object
                || !metadata.TryGetProperty("Annotations", out var annotations)
                || annotations.ValueKind != JsonValueKind.Object
                || !annotations.TryGetProperty(key, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return value.GetString();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IssueRunRef?> FindIssueForRunAsync(MohistDbContext db, string runId)
    {
        var rows = await db.IssueStates.AsNoTracking()
            .Where(x => x.StateJson.Contains(runId))
            .ToListAsync();

        foreach (var row in rows)
        {
            var issue = TryParseIssueRunRef(row.StateJson, runId);
            if (issue is not null)
                return issue;
        }

        return null;
    }

    private static IssueRunRef? TryParseIssueRunRef(string json, string runId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("WorkflowRunId", out var workflowRunId)
                || workflowRunId.GetString() != runId)
                return null;
            if (!root.TryGetProperty("ProjectId", out var projectIdEl)
                || string.IsNullOrWhiteSpace(projectIdEl.GetString()))
                return null;
            if (!root.TryGetProperty("Id", out var issueIdEl)
                || string.IsNullOrWhiteSpace(issueIdEl.GetString()))
                return null;
            if (!root.TryGetProperty("Number", out var numberEl)
                || !numberEl.TryGetInt32(out var number))
                return null;

            return new IssueRunRef(issueIdEl.GetString()!, projectIdEl.GetString()!, number);
        }
        catch
        {
            return null;
        }
    }

    private async Task<VariableBundle> LoadProjectLayerAsync(MohistDbContext db, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return VariableBundle.Empty;
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

    private static async Task<VariableBundle> LoadIssueLayerAsync(MohistDbContext db, RunContext context)
    {
        var issueProfile = await LoadIssueProfileAsync(db, context);
        return VariableBundle.FromJson(issueProfile?.VariablesJson);
    }

    private static async Task<IssueWorkflowProfileRow?> LoadIssueProfileAsync(MohistDbContext db, RunContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.IssueId))
        {
            var byId = await db.IssueWorkflowProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.IssueKey == context.IssueId);
            if (byId is not null)
                return byId;
        }

        if (string.IsNullOrWhiteSpace(context.LegacyIssueKey))
            return null;

        return await db.IssueWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IssueKey == context.LegacyIssueKey);
    }

    // =======================================================================
    // Prompts
    // =======================================================================

    public async Task<ResolvedPrompt?> LoadPromptAsync(string runId, string key, string? projectId = null, string? issueId = null, string? legacyIssueKey = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var context = await ResolveRunContextAsync(db, runId);
        var pid = string.IsNullOrWhiteSpace(projectId) ? context.ProjectId : projectId;
        var iid = string.IsNullOrWhiteSpace(issueId) ? context.IssueId : issueId;
        var lk = string.IsNullOrWhiteSpace(legacyIssueKey) ? context.LegacyIssueKey : legacyIssueKey;

        // 1. issue prompts
        var issueProfile = await LoadIssueProfileAsync(db, new RunContext(pid, iid, lk));
        if (issueProfile is not null)
        {
            var prompts = DeserializePrompts(issueProfile.PromptsJson);
            if (prompts.TryGetValue(key, out var body))
                return new ResolvedPrompt(key, key, string.Empty, Array.Empty<string>(), null, body, "issue");
        }

        // 2. project prompts
        if (!string.IsNullOrWhiteSpace(pid))
        {
            var row = await db.ProjectPromptTemplates.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProjectId == pid && x.Key == key);
            if (row is not null)
            {
                var systemTemplates = _promptLoader.LoadAllTemplates();
                var source = systemTemplates.ContainsKey(key) ? "project" : "project-new";
                return new ResolvedPrompt(key, row.DisplayName, row.Description,
                    ProjectWorkflowProfileManager.DeserializeTags(row.TagsJson),
                    row.Stage, row.Body, source);
            }
        }

        // 3. system prompts
        var systemTemplatesMap = _promptLoader.LoadAllTemplates();
        if (systemTemplatesMap.TryGetValue(key, out var sys))
            return new ResolvedPrompt(key, sys.DisplayName, sys.Description, sys.Tags, sys.Stage, sys.Body, "system");

        return null;
    }

    public async Task<IReadOnlyList<ResolvedPrompt>> LoadPromptsAsync(string runId, string? stage = null, string? projectId = null, string? issueId = null, string? legacyIssueKey = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var context = await ResolveRunContextAsync(db, runId);
        var pid = string.IsNullOrWhiteSpace(projectId) ? context.ProjectId : projectId;
        var iid = string.IsNullOrWhiteSpace(issueId) ? context.IssueId : issueId;
        var lk = string.IsNullOrWhiteSpace(legacyIssueKey) ? context.LegacyIssueKey : legacyIssueKey;

        var systemTemplates = _promptLoader.LoadAllTemplates();
        var projectRows = !string.IsNullOrWhiteSpace(pid)
            ? await db.ProjectPromptTemplates.AsNoTracking()
                .Where(x => x.ProjectId == pid)
                .ToListAsync()
            : new List<Prompts.Storage.ProjectTemplateRow>();
        var projectByKey = projectRows.ToDictionary(r => r.Key, StringComparer.Ordinal);

        Dictionary<string, string> issuePrompts;
        var issueProfile = await LoadIssueProfileAsync(db, new RunContext(pid, iid, lk));
        if (issueProfile is not null)
            issuePrompts = DeserializePrompts(issueProfile.PromptsJson);
        else
            issuePrompts = new Dictionary<string, string>(StringComparer.Ordinal);

        var keys = new SortedSet<string>(systemTemplates.Keys, StringComparer.Ordinal);
        foreach (var p in projectRows)
            keys.Add(p.Key);
        foreach (var k in issuePrompts.Keys)
            keys.Add(k);

        var results = new List<ResolvedPrompt>();
        foreach (var key in keys)
        {
            if (issuePrompts.TryGetValue(key, out var issueBody))
            {
                results.Add(new ResolvedPrompt(key, key, string.Empty, Array.Empty<string>(), null, issueBody, "issue"));
                continue;
            }

            if (projectByKey.TryGetValue(key, out var pp))
            {
                var source = systemTemplates.ContainsKey(key) ? "project" : "project-new";
                results.Add(new ResolvedPrompt(key, pp.DisplayName, pp.Description,
                    ProjectWorkflowProfileManager.DeserializeTags(pp.TagsJson),
                    pp.Stage, pp.Body, source));
                continue;
            }

            if (systemTemplates.TryGetValue(key, out var sys))
                results.Add(new ResolvedPrompt(key, sys.DisplayName, sys.Description, sys.Tags, sys.Stage, sys.Body, "system"));
        }

        if (!string.IsNullOrWhiteSpace(stage))
            results = results.Where(r => r.Stage is null || string.Equals(r.Stage, stage, StringComparison.OrdinalIgnoreCase)).ToList();

        return results;
    }

    public PromptPreviewResult RenderPrompt(string body, JsonElement variables)
    {
        var (rendered, missing, depth) = _engine.Render(body, variables);
        return new PromptPreviewResult(rendered, missing, depth);
    }

    private static Dictionary<string, string> DeserializePrompts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new(StringComparer.Ordinal);
        }
        catch
        {
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// 处理 task.with:
    ///   - value 是 JSON 对象 且 effectiveVars 中有同名 key: deep merge (vars 覆盖)
    ///   - 其他 (包括 ${{ }} 模板字符串): 保留原值, 由 runner 端展开
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

            if (v.ValueKind == JsonValueKind.Object
                && varsRoot.ValueKind == JsonValueKind.Object
                && varsRoot.TryGetProperty(key, out var varsOverride)
                && varsOverride.ValueKind == JsonValueKind.Object)
            {
                var merged = VariableBundle.DeepMerge(v, varsOverride);
                result[key] = merged;
                continue;
            }

            result[key] = v.Clone();
        }

        return result;
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

    private static async Task<ResolvedTemplate?> LoadTemplateReferenceAsync(
        MohistDbContext db, string projectId, string templateId)
    {
        var projectTemplate = await LoadProjectTemplateAsync(db, projectId, templateId);
        if (projectTemplate is not null)
            return projectTemplate;

        var systemDefinition = ProjectWorkflowProfileManager.GetSystemTemplateDefinition(templateId);
        return systemDefinition is null
            ? null
            : ResolvedTemplate.FromDefinition($"system-template:{templateId}", systemDefinition);
    }

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

    private sealed record RunContext(string? ProjectId, string? IssueId, string? LegacyIssueKey);
    private sealed record IssueRunRef(string IssueId, string ProjectId, int Number);
    private sealed record LegacyProjectVars(
        Dictionary<string, JsonElement?>? Vars = null,
        Dictionary<string, LegacyProjectStageVars?>? Stages = null);

    private sealed record LegacyProjectStageVars(
        Dictionary<string, JsonElement?>? Vars = null);
}
