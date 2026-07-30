using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Workflow.Services;

public class WorkflowPromptResolver : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ProjectPromptStore _projectPrompts;

    public WorkflowPromptResolver(
        IDbContextFactory<MohistDbContext> dbFactory,
        ProjectPromptStore projectPrompts)
    {
        _dbFactory = dbFactory;
        _projectPrompts = projectPrompts;
    }

    public async Task<ResolvedPrompt?> LoadPromptAsync(
        string runId,
        string key,
        string? projectId = null)
    {
        var context = await ResolveRunContextAsync(runId);
        var effective = await _projectPrompts.GetPromptAsync(
            string.IsNullOrWhiteSpace(projectId) ? context.ProjectId ?? string.Empty : projectId,
            key);
        return effective is null ? null : ToResolvedPrompt(effective);
    }

    public async Task<IReadOnlyList<ResolvedPrompt>> LoadPromptsAsync(
        string runId,
        string? stage = null,
        string? projectId = null)
    {
        var context = await ResolveRunContextAsync(runId);
        var prompts = await _projectPrompts.ListPromptsAsync(
            string.IsNullOrWhiteSpace(projectId) ? context.ProjectId ?? string.Empty : projectId,
            stage);
        return prompts.Select(ToResolvedPrompt).ToList();
    }

    public PromptPreviewResult RenderPrompt(string body, JsonElement variables) =>
        _projectPrompts.RenderPrompt(body, variables);

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

    private static async Task<IssueRunRef?> FindIssueForRunAsync(
        MohistDbContext db,
        string runId)
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
            if (!root.TryGetProperty("projectId", out var projectId)
                || string.IsNullOrWhiteSpace(projectId.GetString()))
                return null;
            if (!root.TryGetProperty("number", out var number)
                || !number.TryGetInt32(out var issueNumber))
                return null;

            return new IssueRunRef(projectId.GetString()!, issueNumber);
        }
        catch
        {
            return null;
        }
    }

    private static ResolvedPrompt ToResolvedPrompt(EffectivePrompt prompt) =>
        new(prompt.Key, prompt.DisplayName, prompt.Description, prompt.Tags, prompt.Stage, prompt.Body, prompt.Source);

    private sealed record RunContext(string? ProjectId, int? IssueNumber);
    private sealed record IssueRunRef(string ProjectId, int Number);
}
