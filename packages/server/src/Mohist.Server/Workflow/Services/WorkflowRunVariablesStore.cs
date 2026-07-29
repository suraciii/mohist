using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Services;

public class WorkflowRunVariablesStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowRunVariablesStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<VariableBundle> GetVariablesAsync(string workflowRunId)
    {
        var row = await LoadRowAsync(workflowRunId);
        return row is null
            ? VariableBundle.Empty
            : VariableBundle.FromJson(row.Variables);
    }

    public async Task<VariableBundle> SetVariablesAsync(string workflowRunId, VariableBundle bundle)
    {
        VariableBundleShapeValidator.Validate(bundle);
        return await MutateVariablesAsync(workflowRunId, _ => bundle);
    }

    public async Task<VariableBundle> PatchVariablesAsync(string workflowRunId, VariableBundle patch)
    {
        VariableBundleShapeValidator.Validate(patch);
        return await MutateVariablesAsync(
            workflowRunId,
            current => VariableBundle.Patch(
                current is null ? VariableBundle.Empty : VariableBundle.FromJson(current.Variables),
                patch));
    }

    private async Task<VariableBundle> MutateVariablesAsync(
        string workflowRunId,
        Func<WorkflowRunProfileRow?, VariableBundle> buildDesiredExplicit)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowRunProfiles
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);

        var desiredExplicit = buildDesiredExplicit(row);
        if (row is null)
        {
            row = new WorkflowRunProfileRow
            {
                WorkflowRunId = workflowRunId,
                Variables = desiredExplicit.ToJson(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.WorkflowRunProfiles.Add(row);
            db.Entry(row).Property<long>("ETag").CurrentValue = 1;
        }
        else
        {
            row.Variables = desiredExplicit.ToJson();
            row.UpdatedAt = DateTimeOffset.UtcNow;
            BumpETag(db.Entry(row));
        }

        await db.SaveChangesAsync();

        return desiredExplicit;
    }

    private static void BumpETag(EntityEntry<WorkflowRunProfileRow> entry)
    {
        var etag = entry.Property<long>("ETag");
        etag.CurrentValue = etag.OriginalValue + 1;
    }

    private async Task<WorkflowRunProfileRow?> LoadRowAsync(string workflowRunId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.WorkflowRunProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);
    }

}
