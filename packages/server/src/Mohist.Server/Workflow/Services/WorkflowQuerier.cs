using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Services;

public class WorkflowQuerier
{
    private readonly IDbContextFactory<MohistDbContext> _db;
    private readonly WorkflowProfileManager _profileManager;

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

    public WorkflowQuerier(IDbContextFactory<MohistDbContext> db, WorkflowProfileManager profileManager)
    {
        _db = db;
        _profileManager = profileManager;
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

        return WorkflowStatusMapper.BuildStatusView(run, definition);
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
