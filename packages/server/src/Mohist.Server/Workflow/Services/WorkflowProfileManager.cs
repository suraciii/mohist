using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Infrastructure.Data.Workflow;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Workflow template/variables/prompts resolution entrypoint.
///
/// Template resolution precedence (highest first):
///   1. Issue custom YAML (issue_workflow_profile.Template)
///   2. Issue referenced template (issue_workflow_profile.SourceTemplateId)
///   3. Issue's effective workflow profile (issue.WorkflowProfileId →
///      project default template → first enabled system profile).
/// </summary>
public class WorkflowProfileManager : IScopedService
{
    internal const string NoEnabledWorkflowProfileMessage =
        "No enabled workflow profile is available. Enable a workflow first.";

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IPromptLoader _promptLoader;
    private readonly PromptTemplateEngine _engine;
    private readonly ConfigService _configService;
    private readonly WorkflowRunProfileManager _runProfileManager;

    public WorkflowProfileManager(
        IDbContextFactory<MohistDbContext> dbFactory,
        IPromptLoader promptLoader,
        PromptTemplateEngine engine,
        ConfigService configService,
        WorkflowRunProfileManager runProfileManager)
    {
        _dbFactory = dbFactory;
        _promptLoader = promptLoader;
        _engine = engine;
        _configService = configService;
        _runProfileManager = runProfileManager;
    }

    public async Task<ResolvedTemplate> LoadTemplateAsync(
        string runId,
        string? projectId = null,
        int? issueNumber = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var resolvedContext = await ResolveRunContextAsync(db, runId);
        var context = new RunContext(
            string.IsNullOrWhiteSpace(projectId) ? resolvedContext.ProjectId : projectId,
            issueNumber ?? resolvedContext.IssueNumber,
            resolvedContext.RunExists);

        var profileContext = await ResolveEffectiveProfileContextAsync(db, context);
        if (!string.IsNullOrWhiteSpace(context.ProjectId)
            && string.IsNullOrWhiteSpace(profileContext.EffectiveProfileId))
        {
            throw new InvalidOperationException(NoEnabledWorkflowProfileMessage);
        }

        var issueProfile = await LoadIssueProfileAsync(db, context);

        if (issueProfile is not null && !string.IsNullOrWhiteSpace(issueProfile.Template))
        {
            var issueDef = DeserializeDefinition(issueProfile.Template);
            if (issueDef is not null && !string.IsNullOrWhiteSpace(issueDef.Id))
                return ResolvedTemplate.FromDefinition($"issue-custom:{issueProfile.ProjectId}#{issueProfile.IssueNumber}", issueDef);
        }

        if (issueProfile is not null
            && !string.IsNullOrWhiteSpace(issueProfile.SourceTemplateId)
            && !string.IsNullOrWhiteSpace(context.ProjectId))
        {
            var template = await LoadTemplateReferenceAsync(db, context.ProjectId, issueProfile.SourceTemplateId);
            if (template is not null)
                return template;
        }

        if (string.IsNullOrWhiteSpace(profileContext.IssueSelection)
            && !string.IsNullOrWhiteSpace(profileContext.ProjectDefaultId)
            && (ProjectWorkflowProfileManager.GetSystemTemplateInfo(profileContext.ProjectDefaultId) is null
                || string.Equals(profileContext.ProjectDefaultId, profileContext.EffectiveProfileId, StringComparison.Ordinal))
            && !string.IsNullOrWhiteSpace(context.ProjectId))
        {
            var projectDefault = await LoadTemplateReferenceAsync(db, context.ProjectId, profileContext.ProjectDefaultId);
            if (projectDefault is not null)
                return projectDefault;
        }

        if (string.IsNullOrWhiteSpace(profileContext.EffectiveProfileId))
            throw new InvalidOperationException(NoEnabledWorkflowProfileMessage);

        var effectiveDefinition = ProjectWorkflowProfileManager.GetSystemTemplateDefinition(profileContext.EffectiveProfileId);
        if (effectiveDefinition is not null)
        {
            return ResolvedTemplate.FromDefinition(
                $"system-template:{profileContext.EffectiveProfileId}",
                effectiveDefinition);
        }

        throw new InvalidOperationException(NoEnabledWorkflowProfileMessage);
    }

    // =======================================================================
    // Narrow APIs — encapsulate the full template selection cascade so the
    // control-plane grain never has to touch a WorkflowDefinition. Each call
    // re-runs LoadTemplateAsync so profile mutations (issue/profile template
    // edits) become visible to subsequent callers. For stage-init callers this
    // is the hot-reload hook; for Create and RequestChanges it costs the same
    // one extra cascade the control plane already paid.
    // =======================================================================

    /// <summary>
    /// Returns the per-stage spec (tasks + checks + lock behavior) for a
    /// single stage. Re-runs the cascade on every call so subsequent stages
    /// see live profile edits (hot reload per stage-enter). Throws if the
    /// resolved template does not contain <paramref name="stageId"/>.
    /// </summary>
    public async Task<StageDefinition> LoadStageSpecsAsync(
        string runId,
        string stageId,
        string? projectId = null,
        int? issueNumber = null)
    {
        var template = await LoadTemplateAsync(runId, projectId, issueNumber);
        var definition = template.Structure
            ?? throw new InvalidOperationException(
                $"Workflow '{runId}' has no effective workflow template");
        var stage = definition.Stages.Find(s => string.Equals(s.Stage, stageId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Workflow '{runId}' has no definition for stage '{stageId}'");
        return stage;
    }

    /// <summary>
    /// Returns just the workflow's stage sequence and approval flags — enough
    /// to construct a <see cref="WorkflowRun"/> aggregate without pulling tasks,
    /// checks, or lock configuration across the grain boundary. Used by the
    /// grain's <c>StartAsync</c> path.
    /// </summary>
    public async Task<WorkflowStructure> LoadStructureAsync(
        string runId,
        string? projectId = null,
        int? issueNumber = null)
    {
        var template = await LoadTemplateAsync(runId, projectId, issueNumber);
        var definition = template.Structure
            ?? throw new InvalidOperationException(
                $"Workflow '{runId}' has no effective workflow template");
        if (definition.Stages.Count == 0)
            throw new InvalidOperationException(
                $"Workflow '{runId}' has no stages in its effective template");
        return definition.ToStructure();
    }

    /// <summary>
    /// Returns the approval configuration (currently the feedback task config)
    /// from the resolved template. Used by the grain's
    /// <c>RequestChangesAsync</c> path.
    /// </summary>
    public async Task<ApprovalConfig?> LoadApprovalConfigAsync(string runId)
    {
        var template = await LoadTemplateAsync(runId);
        return template.Structure?.Approval;
    }

    private async Task<EffectiveProfileContext> ResolveEffectiveProfileContextAsync(MohistDbContext db, RunContext context)
    {
        string? issueSelection = null;
        if (context.IssueNumber is > 0 && !string.IsNullOrWhiteSpace(context.ProjectId))
        {
            var row = await db.Issues.AsNoTracking()
                .FirstOrDefaultAsync(r => r.ProjectId == context.ProjectId && r.Number == context.IssueNumber);
            issueSelection = ReadWorkflowProfileId(row?.State);
        }

        string? projectDefaultId = null;
        IReadOnlyCollection<string>? disabledIds = null;
        if (!string.IsNullOrWhiteSpace(context.ProjectId))
        {
            var projectProfile = await db.ProjectWorkflowProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProjectId == context.ProjectId);
            projectDefaultId = projectProfile?.DefaultTemplateId;
            disabledIds = context.RunExists ? null : projectProfile?.DisabledWorkflowProfileIds;
        }

        var effectiveProfileId = WorkflowProfileCatalog.ResolveEffectiveProfileId(
            issueSelection,
            projectDefaultId,
            disabledIds);
        return new EffectiveProfileContext(issueSelection, projectDefaultId, effectiveProfileId);
    }

    private sealed record EffectiveProfileContext(
        string? IssueSelection,
        string? ProjectDefaultId,
        string? EffectiveProfileId);

    private async Task<VariableBundle> ResolveConfiguredVariablesAsync(string runId)
    {
        // Resolution merges four live layers, lowest priority first:
        //   1. global config.jsonc bundle (ConfigService.GetVariables)
        //   2. project Variables (ProjectWorkflowProfile.Variables)
        //   3. issue Variables (IssueWorkflowProfile.Variables)
        //   4. run Variables (WorkflowRunProfile.Variables — written by setVars)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var context = await ResolveRunContextAsync(db, runId);
        var global = await _configService.GetVariables();
        var project = await LoadProjectLayerAsync(db, context);
        var issue = await LoadIssueLayerAsync(db, context);
        var run = await _runProfileManager.GetVariablesAsync(runId);
        return VariableBundle.MergeAll(global, project, issue, run);
    }

    internal async Task<VariableBundle> ResolveLayeredVariablesAsync(string runId)
    {
        var template = await LoadTemplateAsync(runId);
        var independent = await ResolveConfiguredVariablesAsync(runId);
        var embedded = template.EmbeddedVariables ?? VariableBundle.Empty;
        return VariableBundle.Patch(embedded, independent);
    }

    public async Task<JsonElement> ResolveEffectiveVariablesAsync(string runId, string? stage)
    {
        var resolved = await ResolveEffectiveVariableBundleAsync(runId, stage);
        return resolved.Vars ?? JSON.DeserializeElement("{}");
    }

    internal async Task<VariableBundle> ResolveEffectiveVariableBundleAsync(string runId, string? stage)
    {
        var layered = await ResolveLayeredVariablesAsync(runId);
        var stageVariables = layered.ResolveStageVars(stage);
        var (repository, workspace, projectId, issueNumber) = await LoadRoutingSnapshotAsync(runId);

        if (repository is null && workspace is null)
            return new VariableBundle(stageVariables, layered.Stages);

        var authoritative = AuthoritativeRoutingOverlay.Apply(
            stageVariables,
            runId,
            repository,
            workspace,
            projectId,
            issueNumber);
        return new VariableBundle(authoritative.Vars, layered.Stages);
    }

    private async Task<(
        Mohist.Server.Workflow.Domain.Run.WorkflowRepositoryContext? Repository,
        Mohist.Server.Workflow.Domain.Run.WorkspaceIdentity? Workspace,
        string? ProjectId,
        int? IssueNumber)>
        LoadRoutingSnapshotAsync(string runId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowRuns.AsNoTracking()
            .Where(x => x.WorkflowRunId == runId)
            .Select(x => new { x.State })
            .FirstOrDefaultAsync();
        if (row is null) return (null, null, null, null);

        var run = TryDeserializeRun(row.State);
        if (run is null) return (null, null, null, null);

        var projectId = run.Metadata?.Annotations is { } a
            && a.TryGetValue("projectId", out var pid)
            && !string.IsNullOrWhiteSpace(pid) ? pid : null;
        int? issueNumber = null;
        if (run.Metadata?.Annotations is { } an
            && an.TryGetValue("issueNumber", out var numText)
            && int.TryParse(numText, out var num))
        {
            issueNumber = num;
        }

        return (run.Repository, run.Workspace, projectId, issueNumber);
    }

    private static Mohist.Server.Workflow.Domain.Run.WorkflowRun? TryDeserializeRun(string? stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<Mohist.Server.Workflow.Domain.Run.WorkflowRun>(
                stateJson, JSON.Options);
        }
        catch
        {
            return null;
        }
    }

    public async Task<WorkspaceIdentity?> LoadIssueWorkspaceAsync(string projectId, int issueNumber)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var issueProfile = await LoadIssueProfileAsync(db, new RunContext(projectId, issueNumber));
        var vars = VariableBundle.FromJson(issueProfile?.Variables).Vars;
        if (vars is not { ValueKind: JsonValueKind.Object }
            || !vars.Value.TryGetProperty("workspace", out var workspace)
            || workspace.ValueKind != JsonValueKind.Object
            || !workspace.TryGetProperty("path", out var path)
            || string.IsNullOrWhiteSpace(path.GetString()))
        {
            return null;
        }

        return new WorkspaceIdentity(
            path.GetString()!,
            workspace.TryGetProperty("branch", out var branch) ? branch.GetString() : null,
            workspace.TryGetProperty("changeDir", out var changeDir) ? changeDir.GetString() : null);
    }

    private static async Task<RunContext> ResolveRunContextAsync(MohistDbContext db, string runId)
    {
        var workflowRun = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == runId);

        var projectId = workflowRun?.MetadataProjectId;
        var issueNumber = workflowRun?.IssueNumber;
        var issue = await FindIssueForRunAsync(db, runId);
        projectId = string.IsNullOrWhiteSpace(projectId) ? issue?.ProjectId : projectId;
        issueNumber ??= issue?.Number;

        return new RunContext(projectId, issueNumber, workflowRun is not null);
    }

    private static string? ReadWorkflowProfileId(string? stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(stateJson);
            return doc.RootElement.TryGetProperty("workflowProfileId", out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
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
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("workflowRunId", out var workflowRunId)
                || workflowRunId.GetString() != runId)
                return null;
            if (!root.TryGetProperty("projectId", out var projectIdEl)
                || string.IsNullOrWhiteSpace(projectIdEl.GetString()))
                return null;
            if (!root.TryGetProperty("number", out var numberEl)
                || !numberEl.TryGetInt32(out var number))
                return null;

            return new IssueRunRef(projectIdEl.GetString()!, number);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<VariableBundle> LoadIssueLayerAsync(MohistDbContext db, RunContext context)
    {
        var issueProfile = await LoadIssueProfileAsync(db, context);
        return VariableBundle.FromJson(issueProfile?.Variables);
    }

    private static async Task<VariableBundle> LoadProjectLayerAsync(MohistDbContext db, RunContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ProjectId))
            return VariableBundle.Empty;

        var projectProfile = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == context.ProjectId);
        return VariableBundle.FromJson(projectProfile?.Variables);
    }

    private static async Task<IssueWorkflowProfile?> LoadIssueProfileAsync(MohistDbContext db, RunContext context)
    {
        if (context.IssueNumber is > 0 && !string.IsNullOrWhiteSpace(context.ProjectId))
        {
            var byId = await db.IssueWorkflowProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProjectId == context.ProjectId && x.IssueNumber == context.IssueNumber);
            if (byId is not null)
                return byId;
        }

        return null;
    }

    // =======================================================================
    // Prompts
    // =======================================================================

    public async Task<ResolvedPrompt?> LoadPromptAsync(string runId, string key, string? projectId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var context = await ResolveRunContextAsync(db, runId);
        var pid = string.IsNullOrWhiteSpace(projectId) ? context.ProjectId : projectId;

        // Project prompts replace builtin bodies by key.
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

        var systemTemplatesMap = _promptLoader.LoadAllTemplates();
        if (systemTemplatesMap.TryGetValue(key, out var sys))
            return new ResolvedPrompt(key, sys.DisplayName, sys.Description, sys.Tags, sys.Stage, sys.Body, "system");

        return null;
    }

    public async Task<IReadOnlyList<ResolvedPrompt>> LoadPromptsAsync(string runId, string? stage = null, string? projectId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var context = await ResolveRunContextAsync(db, runId);
        var pid = string.IsNullOrWhiteSpace(projectId) ? context.ProjectId : projectId;

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

        var keys = new SortedSet<string>(systemTemplates.Keys, StringComparer.Ordinal);
        foreach (var k in projectPrompts.Keys)
            keys.Add(k);

        var results = new List<ResolvedPrompt>();
        foreach (var key in keys)
        {
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

    public static Dictionary<string, JsonElement?>? ExpandTaskWith(
        VariableBundle? effectiveVars,
        Dictionary<string, JsonElement?>? taskWith) =>
        TaskWithExpander.Expand(effectiveVars, taskWith);

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
            return JsonSerializer.Deserialize<WorkflowDefinition>(json, Mohist.Server.Infrastructure.JSON.Options);
        }
        catch
        {
            return null;
        }
    }

    private sealed record RunContext(string? ProjectId, int? IssueNumber, bool RunExists = false);
    private sealed record IssueRunRef(string ProjectId, int Number);
}
