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

        var leaseJson = await db.WorkflowLeases.AsNoTracking()
            .Where(e => e.WorkflowRunId == workflowRunId)
            .Select(e => e.State)
            .FirstOrDefaultAsync();
        var lease = leaseJson is not null && leaseJson != "null"
            ? JsonSerializer.Deserialize<WorkLease>(leaseJson, StorageJsonOptions)
            : null;

        return WorkflowStatusMapper.BuildStatusView(run, definition, lease);
    }

    public async Task<WorkflowVariablesView?> GetVariablesAsync(string workflowRunId)
    {
        await using var db = await _db.CreateDbContextAsync();
        var json = await db.WorkflowVariables.AsNoTracking()
            .Where(e => e.WorkflowRunId == workflowRunId)
            .Select(e => e.State)
            .FirstOrDefaultAsync();

        if (json is null) return null;

        var ctx = JsonSerializer.Deserialize<VariablesDto>(json, StorageJsonOptions);
        return ctx is null
            ? null
            : new WorkflowVariablesView(ctx.Json, ctx.StageVariables);
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
