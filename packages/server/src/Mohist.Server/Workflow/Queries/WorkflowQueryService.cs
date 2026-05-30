using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Views;
using Mohist.Server.Workflow.Infrastructure;

namespace Mohist.Server.Workflow.Queries;

public class WorkflowQueryService
{
    private readonly IDbContextFactory<MohistDbContext> _db;

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

    public WorkflowQueryService(IDbContextFactory<MohistDbContext> db)
    {
        _db = db;
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

        var profileJson = await db.WorkflowRunProfiles.AsNoTracking()
            .Where(e => e.Key == workflowRunId)
            .Select(e => e.StateJson)
            .FirstOrDefaultAsync();
        var profile = profileJson is not null
            ? JsonSerializer.Deserialize<WorkflowRunProfile>(profileJson)
            : null;

        var leaseJson = await db.WorkflowLeases.AsNoTracking()
            .Where(e => e.WorkflowRunId == workflowRunId)
            .Select(e => e.StateJson)
            .FirstOrDefaultAsync();
        var lease = leaseJson is not null && leaseJson != "null"
            ? JsonSerializer.Deserialize<WorkLease>(leaseJson, StorageJsonOptions)
            : null;

        return WorkflowStatusMapper.BuildStatusView(run, profile, lease);
    }

    public async Task<WorkflowVariablesView?> GetVariablesAsync(string workflowRunId)
    {
        await using var db = await _db.CreateDbContextAsync();
        var json = await db.WorkflowVariables.AsNoTracking()
            .Where(e => e.WorkflowRunId == workflowRunId)
            .Select(e => e.StateJson)
            .FirstOrDefaultAsync();

        if (json is null) return null;

        var ctx = JsonSerializer.Deserialize<VariablesDto>(json, StorageJsonOptions);
        return ctx is null
            ? null
            : new WorkflowVariablesView(ctx.Json, ctx.StageVariables);
    }

    public async Task<string?> GetDefinitionYamlAsync(string workflowRunId)
    {
        await using var db = await _db.CreateDbContextAsync();
        var profileJson = await db.WorkflowRunProfiles.AsNoTracking()
            .Where(e => e.Key == workflowRunId)
            .Select(e => e.StateJson)
            .FirstOrDefaultAsync();

        if (profileJson is null) return null;

        var profile = JsonSerializer.Deserialize<WorkflowRunProfile>(profileJson);
        if (profile is null) return null;

        var definition = new WorkflowDefinition(workflowRunId, profile.Definition.Stages);
        return WorkflowYamlSerializer.ToYaml(definition);
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
