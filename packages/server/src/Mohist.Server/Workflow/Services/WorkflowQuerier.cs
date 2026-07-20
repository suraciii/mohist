using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using WorkspaceIdentity = Mohist.Server.Workflow.Domain.Run.WorkspaceIdentity;

namespace Mohist.Server.Workflow.Services;

public class WorkflowQuerier : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _db;
    private readonly WorkflowProfileManager _profileManager;
    private readonly IWorkflowArtifactQuerier _artifactQuerier;

    public WorkflowQuerier(
        IDbContextFactory<MohistDbContext> db,
        WorkflowProfileManager profileManager,
        IWorkflowArtifactQuerier artifactQuerier)
    {
        _db = db;
        _profileManager = profileManager;
        _artifactQuerier = artifactQuerier;
    }

    public async Task<WorkflowStatusView?> GetStatusAsync(string workflowRunId)
    {
        await using var db = await _db.CreateDbContextAsync();

        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(e => e.WorkflowRunId == workflowRunId);
        var run = row is null ? null : Hydrate(row);
        if (run is null) return null;

        var definition = (await _profileManager.LoadTemplateAsync(workflowRunId)).Structure;

        var view = WorkflowStatusMapper.BuildStatusView(run, definition);
        if (view is null) return null;

        await AttachArtifactSummariesAsync(view, workflowRunId);

        return view;
    }

    private async Task AttachArtifactSummariesAsync(WorkflowStatusView view, string workflowRunId)
    {
        var artifacts = await _artifactQuerier.ListAsync(workflowRunId);
        if (artifacts.Count == 0) return;

        var byTaskRun = artifacts
            .GroupBy(a => a.TaskRunId)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var stage in view.Stages)
        {
            for (var i = 0; i < stage.Tasks.Count; i++)
            {
                var task = stage.Tasks[i];
                if (!byTaskRun.TryGetValue(task.Id, out var taskArtifacts)) continue;

                var summaries = taskArtifacts
                    .Select(a => new ArtifactSummaryView(
                        a.ArtifactId,
                        a.Path,
                        a.Kind,
                        a.DisplayName,
                        a.RecordedAt,
                        a.Size))
                    .ToList();

                stage.Tasks[i] = new TaskStatusView(
                    task.Id,
                    task.Title,
                    task.Uses,
                    task.Status,
                    task.RequiredFiles,
                    task.Classification,
                    SessionName: task.SessionName,
                    ArtifactSummaries: summaries,
                    StartedAt: task.StartedAt,
                    CompletedAt: task.CompletedAt,
                    DurationMs: task.DurationMs,
                    Output: task.Output,
                    Error: task.Error);
            }
        }
    }

    public async Task<WorkspaceIdentity?> GetWorkspaceAsync(string workflowRunId)
    {
        await using var db = await _db.CreateDbContextAsync();

        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(e => e.WorkflowRunId == workflowRunId);
        var run = row is null ? null : Hydrate(row);
        return run?.Workspace;
    }

    /// <summary>
    /// issue-417 T-006 (D4): returns the immutable repository context
    /// the run captured at start time, or <c>null</c> when the run
    /// has none (generic / non-Issue-backed runs) or when the run
    /// state cannot be loaded. The rebase / review / cleanup routes
    /// load this and never recurse into the live Project metadata,
    /// so a terminal Issue whose repository declaration is later
    /// removed can still drive cleanup against its original
    /// snapshot.
    /// </summary>
    public async Task<WorkflowRepositoryContext?> GetRepositoryContextAsync(string workflowRunId)
    {
        await using var db = await _db.CreateDbContextAsync();

        var runJson = await db.WorkflowRuns.AsNoTracking()
            .Where(e => e.WorkflowRunId == workflowRunId)
            .Select(e => e.State)
            .FirstOrDefaultAsync();
        if (runJson is null) return null;

        var run = DeserializeWorkflowRun(runJson);
        return run?.Repository;
    }

    public async Task<JsonElement> GetEffectiveVariablesAsync(string workflowRunId, string? stage = null)
    {
        return await _profileManager.ResolveEffectiveVariablesAsync(workflowRunId, stage);
    }

    public async Task<JsonElement> GetEffectiveVariableAsync(string workflowRunId, string keyPath, string? stage = null)
    {
        var variables = await GetEffectiveVariablesAsync(workflowRunId, stage);
        return VariableBundle.GetByKeyPath(variables, keyPath);
    }

    public async Task<string?> GetDefinitionYamlAsync(string workflowRunId)
    {
        var definition = (await _profileManager.LoadTemplateAsync(workflowRunId)).Structure;
        return definition is null ? null : WorkflowYamlSerializer.ToYaml(definition);
    }

    public async Task<bool> HasIncompleteTaskWithUsesAsync(string workflowRunId, string uses)
    {
        await using var db = await _db.CreateDbContextAsync();
        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(e => e.WorkflowRunId == workflowRunId);
        var run = row is null ? null : Hydrate(row);
        return run?.HasIncompleteTaskWithUses(uses) ?? false;
    }

    public async Task<bool> HasIncompleteTaskByIdAsync(string workflowRunId, string id)
    {
        await using var db = await _db.CreateDbContextAsync();
        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(e => e.WorkflowRunId == workflowRunId);
        var run = row is null ? null : Hydrate(row);
        return run?.HasIncompleteTaskById(id) ?? false;
    }

    private static WorkflowRun? DeserializeWorkflowRun(string json) =>
        JsonSerializer.Deserialize<WorkflowRun>(WorkflowRunStore.MigrateLegacyWorkflowRunJson(json), JSON.Options);

    private static WorkflowRun? Hydrate(WorkflowRunRow row)
    {
        var run = DeserializeWorkflowRun(row.State);
        if (run is not null)
            WorkflowRunLineage.RestoreStoredEpicNumber(run, row.EpicNumber);
        return run;
    }

}
