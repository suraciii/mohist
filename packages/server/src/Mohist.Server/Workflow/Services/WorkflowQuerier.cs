using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using WorkspaceIdentity = Mohist.Server.Workflow.Domain.Run.WorkspaceIdentity;

namespace Mohist.Server.Workflow.Services;

public class WorkflowQuerier
{
    private readonly IDbContextFactory<MohistDbContext> _db;
    private readonly WorkflowProfileManager _profileManager;
    private readonly IWorkflowArtifactQuerier _artifactQuerier;

    private static readonly JsonSerializerOptions RunJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions StorageJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

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

        var runJson = await db.WorkflowRuns.AsNoTracking()
            .Where(e => e.WorkflowRunId == workflowRunId)
            .Select(e => e.State)
            .FirstOrDefaultAsync();
        if (runJson is null) return null;

        var run = JsonSerializer.Deserialize<WorkflowRun>(runJson, RunJsonOptions);
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
                    ArtifactSummaries: summaries);
            }
        }
    }

    public async Task<WorkspaceIdentity?> GetWorkspaceAsync(string workflowRunId)
    {
        await using var db = await _db.CreateDbContextAsync();

        var runJson = await db.WorkflowRuns.AsNoTracking()
            .Where(e => e.WorkflowRunId == workflowRunId)
            .Select(e => e.State)
            .FirstOrDefaultAsync();
        if (runJson is null) return null;

        var run = JsonSerializer.Deserialize<WorkflowRun>(runJson, RunJsonOptions);
        return run?.Workspace;
    }

    public async Task<WorkflowVariablesView?> GetVariablesAsync(string workflowRunId)
    {
        var bundle = await _profileManager.LoadVariablesAsync(workflowRunId);
        if (!bundle.Vars.HasValue && bundle.Stages is null)
            return null;

        var varsJson = bundle.Vars.HasValue
            ? JsonSerializer.Serialize(bundle.Vars.Value, RunJsonOptions)
            : "{}";

        Dictionary<string, Dictionary<string, string>>? stageMap = null;
        if (bundle.Stages is not null)
        {
            stageMap = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var (stage, stageVars) in bundle.Stages)
            {
                if (!stageVars.Vars.HasValue) continue;
                var stageVarsJson = JsonSerializer.Serialize(stageVars.Vars.Value, RunJsonOptions);
                var inner = JsonSerializer.Deserialize<Dictionary<string, string>>(stageVarsJson, RunJsonOptions);
                if (inner is not null) stageMap[stage] = inner;
            }
        }

        return new WorkflowVariablesView(varsJson, stageMap);
    }

    public async Task<string?> GetDefinitionYamlAsync(string workflowRunId)
    {
        var definition = (await _profileManager.LoadTemplateAsync(workflowRunId)).Structure;
        return definition is null ? null : WorkflowYamlSerializer.ToYaml(definition);
    }

    public async Task<bool> HasIncompleteTaskWithUsesAsync(string workflowRunId, string uses)
    {
        await using var db = await _db.CreateDbContextAsync();
        var runJson = await db.WorkflowRuns.AsNoTracking()
            .Where(e => e.WorkflowRunId == workflowRunId)
            .Select(e => e.State)
            .FirstOrDefaultAsync();

        var run = runJson is not null
            ? JsonSerializer.Deserialize<WorkflowRun>(runJson, RunJsonOptions)
            : null;
        return run?.HasIncompleteTaskWithUses(uses) ?? false;
    }

    public async Task<bool> HasIncompleteTaskByIdAsync(string workflowRunId, string id)
    {
        await using var db = await _db.CreateDbContextAsync();
        var runJson = await db.WorkflowRuns.AsNoTracking()
            .Where(e => e.WorkflowRunId == workflowRunId)
            .Select(e => e.State)
            .FirstOrDefaultAsync();

        var run = runJson is not null
            ? JsonSerializer.Deserialize<WorkflowRun>(runJson, RunJsonOptions)
            : null;
        return run?.HasIncompleteTaskById(id) ?? false;
    }

    private sealed record VariablesDto(string Json, Dictionary<string, Dictionary<string, string>>? StageVariables);
}
