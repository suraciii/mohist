using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Infrastructure.Data.Workflow;
using IssueWorkflowProfiles = Mohist.Server.Issue.Services.WorkflowProfiles.IssueWorkflowProfiles;

namespace Mohist.Server.Workflow.Services;

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
        string? issueId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var resolvedContext = await ResolveRunContextAsync(db, runId);
        var context = new RunContext(
            string.IsNullOrWhiteSpace(projectId) ? resolvedContext.ProjectId : projectId,
            string.IsNullOrWhiteSpace(issueId) ? resolvedContext.IssueId : issueId);

        var issueProfile = await LoadIssueProfileAsync(db, context);

        if (issueProfile is not null && !string.IsNullOrWhiteSpace(issueProfile.Template))
        {
            var issueDef = DeserializeDefinition(issueProfile.Template);
            if (issueDef is not null && !string.IsNullOrWhiteSpace(issueDef.Id))
                return ResolvedTemplate.FromDefinition($"issue-custom:{issueProfile.IssueId}", issueDef);
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
        var issue = await FindIssueForRunAsync(db, runId);
        projectId = string.IsNullOrWhiteSpace(projectId) ? issue?.ProjectId : projectId;
        issueId = string.IsNullOrWhiteSpace(issueId) ? issue?.IssueId : issueId;

        return new RunContext(projectId, issueId);
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
        var rows = await db.Issues.AsNoTracking()
            .Where(x => x.WorkflowRunId == runId)
            .ToListAsync();

        foreach (var row in rows)
        {
            var issue = TryParseIssueRunRef(row.State, runId);
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
        return VariableBundle.FromJson(projectProfile?.Variables);
    }

    private static async Task<VariableBundle> LoadIssueLayerAsync(MohistDbContext db, RunContext context)
    {
        var issueProfile = await LoadIssueProfileAsync(db, context);
        return VariableBundle.FromJson(issueProfile?.Variables);
    }

    private static async Task<IssueWorkflowProfile?> LoadIssueProfileAsync(MohistDbContext db, RunContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.IssueId))
        {
            var byId = await db.IssueWorkflowProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.IssueId == context.IssueId);
            if (byId is not null)
                return byId;
        }

        return null;
    }

    // =======================================================================
    // Prompts
    // =======================================================================

    public async Task<ResolvedPrompt?> LoadPromptAsync(string runId, string key, string? projectId = null, string? issueId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var context = await ResolveRunContextAsync(db, runId);
        var pid = string.IsNullOrWhiteSpace(projectId) ? context.ProjectId : projectId;
        var iid = string.IsNullOrWhiteSpace(issueId) ? context.IssueId : issueId;

        // 1. issue prompts
        var issueProfile = await LoadIssueProfileAsync(db, new RunContext(pid, iid));
        if (issueProfile is not null)
        {
            if (issueProfile.Prompts.TryGetValue(key, out var body))
                return new ResolvedPrompt(key, key, string.Empty, Array.Empty<string>(), null, body, "issue");
        }

        // 2. project prompts
        if (!string.IsNullOrWhiteSpace(pid))
        {
            var projectProfile = await db.ProjectWorkflowProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProjectId == pid);
            if (projectProfile is not null)
            {
                if (projectProfile.Prompts.TryGetValue(key, out var body))
                {
                    var systemTemplates = _promptLoader.LoadAllTemplates();
                    var source = systemTemplates.ContainsKey(key) ? "project" : "project-new";
                    return new ResolvedPrompt(key, key, string.Empty, Array.Empty<string>(), null, body, source);
                }
            }
        }

        // 3. system prompts
        var systemTemplatesMap = _promptLoader.LoadAllTemplates();
        if (systemTemplatesMap.TryGetValue(key, out var sys))
            return new ResolvedPrompt(key, sys.DisplayName, sys.Description, sys.Tags, sys.Stage, sys.Body, "system");

        return null;
    }

    public async Task<IReadOnlyList<ResolvedPrompt>> LoadPromptsAsync(string runId, string? stage = null, string? projectId = null, string? issueId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var context = await ResolveRunContextAsync(db, runId);
        var pid = string.IsNullOrWhiteSpace(projectId) ? context.ProjectId : projectId;
        var iid = string.IsNullOrWhiteSpace(issueId) ? context.IssueId : issueId;

        var systemTemplates = _promptLoader.LoadAllTemplates();
        Dictionary<string, string> projectPrompts;
        if (!string.IsNullOrWhiteSpace(pid))
        {
            var projectProfile = await db.ProjectWorkflowProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProjectId == pid);
            projectPrompts = projectProfile is not null
                ? projectProfile.Prompts
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        else
        {
            projectPrompts = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Dictionary<string, string> issuePrompts;
        var issueProfile = await LoadIssueProfileAsync(db, new RunContext(pid, iid));
        if (issueProfile is not null)
            issuePrompts = issueProfile.Prompts;
        else
            issuePrompts = new Dictionary<string, string>(StringComparer.Ordinal);

        var keys = new SortedSet<string>(systemTemplates.Keys, StringComparer.Ordinal);
        foreach (var k in projectPrompts.Keys)
            keys.Add(k);
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

            if (projectPrompts.TryGetValue(key, out var body))
            {
                var source = systemTemplates.ContainsKey(key) ? "project" : "project-new";
                results.Add(new ResolvedPrompt(key, key, string.Empty, Array.Empty<string>(), null, body, source));
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
        var row = await db.ProjectWorkflowTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TemplateId == templateId);
        if (row is null) return null;

        var def = DeserializeDefinition(row.Template);
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

    private sealed record RunContext(string? ProjectId, string? IssueId);
    private sealed record IssueRunRef(string IssueId, string ProjectId, int Number);
}
