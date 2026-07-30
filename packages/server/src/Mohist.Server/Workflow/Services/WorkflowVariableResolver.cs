using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using WorkspaceIdentity = Mohist.Server.Workflow.Domain.Run.WorkspaceIdentity;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Resolves the Project → Issue → Run variable cascade for a run, plus the
/// stage overlay and the Issue workspace identity. Reads from the three
/// per-scope Stores; no Profile definition lookup.
/// </summary>
public class WorkflowVariableResolver : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ProjectVariableStore _projectVariables;
    private readonly IssueVariableStore _issueVariables;
    private readonly WorkflowRunVariablesStore _runVariables;

    public WorkflowVariableResolver(
        IDbContextFactory<MohistDbContext> dbFactory,
        ProjectVariableStore projectVariables,
        IssueVariableStore issueVariables,
        WorkflowRunVariablesStore runVariables)
    {
        _dbFactory = dbFactory;
        _projectVariables = projectVariables;
        _issueVariables = issueVariables;
        _runVariables = runVariables;
    }

    /// <summary>
    /// Returns the merged Project → Issue → Run variables for the run,
    /// with the requested stage's vars overlaid on top.
    /// </summary>
    public async Task<JsonElement> ResolveEffectiveVariablesAsync(string runId, string? stage)
    {
        var resolved = await ResolveEffectiveVariableBundleAsync(runId, stage);
        return resolved.Vars ?? JSON.DeserializeElement("{}");
    }

    /// <summary>
    /// Same as <see cref="ResolveEffectiveVariablesAsync"/> but returns the
    /// full <see cref="VariableBundle"/> (workflow-wide plus stage
    /// overlay). Preserved for downstream consumers that need the bundle
    /// shape (e.g. dispatch payload assembly).
    /// </summary>
    public async Task<VariableBundle> ResolveEffectiveVariableBundleAsync(string runId, string? stage)
    {
        var layered = await ResolveConfiguredVariablesAsync(runId);
        return new VariableBundle(layered.ResolveStageVars(stage), layered.Stages);
    }

    /// <summary>
    /// Returns the workflow-wide merged variables across the three scopes
    /// (no stage overlay).
    /// </summary>
    public async Task<VariableBundle> ResolveConfiguredVariablesAsync(string runId)
    {
        var context = await ResolveRunContextAsync(runId);
        var project = await LoadProjectLayerAsync(context);
        var issue = await LoadIssueLayerAsync(context);
        var run = await _runVariables.GetVariablesAsync(runId);
        return VariableBundle.MergeAll(project, issue, run);
    }

    /// <summary>
    /// Returns the Issue's workspace identity (path/branch/changeDir)
    /// extracted from its persisted variables. Returns <c>null</c> when
    /// no usable <c>workspace.path</c> is configured for the Issue.
    /// </summary>
    public async Task<WorkspaceIdentity?> LoadIssueWorkspaceAsync(string projectId, int issueNumber)
    {
        var bundle = await _issueVariables.GetVariablesAsync(projectId, issueNumber);
        var vars = bundle.Vars;
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

    private async Task<RunContext> ResolveRunContextAsync(string runId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var workflowRun = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == runId);

        var projectId = workflowRun?.MetadataProjectId;
        var issueNumber = workflowRun?.IssueNumber;
        var issue = await FindIssueForRunAsync(db, runId);
        projectId = string.IsNullOrWhiteSpace(projectId) ? issue?.ProjectId : projectId;
        issueNumber ??= issue?.Number;

        return new RunContext(projectId, issueNumber);
    }

    private async Task<VariableBundle> LoadProjectLayerAsync(RunContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ProjectId))
            return VariableBundle.Empty;
        return await _projectVariables.GetVariablesAsync(context.ProjectId);
    }

    private async Task<VariableBundle> LoadIssueLayerAsync(RunContext context)
    {
        if (context.IssueNumber is > 0 && !string.IsNullOrWhiteSpace(context.ProjectId))
            return await _issueVariables.GetVariablesAsync(context.ProjectId, context.IssueNumber.Value);
        return VariableBundle.Empty;
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

    private sealed record RunContext(string? ProjectId, int? IssueNumber);
    private sealed record IssueRunRef(string ProjectId, int Number);
}
