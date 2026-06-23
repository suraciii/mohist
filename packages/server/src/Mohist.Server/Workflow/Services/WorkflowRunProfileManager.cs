using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Services;

public class WorkflowRunProfileManager
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowRunProfileManager(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<VariableBundle> GetVariablesAsync(string workflowRunId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowRunProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);
        return row is null ? VariableBundle.Empty : VariableBundle.FromJson(row.Variables);
    }

    public async Task<VariableBundle> SetVariablesAsync(string workflowRunId, VariableBundle bundle)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowRunProfiles
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);

        if (row is null)
        {
            row = new WorkflowRunProfileRow
            {
                WorkflowRunId = workflowRunId,
                Variables = bundle.ToJson(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.WorkflowRunProfiles.Add(row);
        }
        else
        {
            row.Variables = bundle.ToJson();
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return bundle;
    }

    public async Task<VariableBundle> PatchVariablesAsync(string workflowRunId, VariableBundle patch)
    {
        var current = await GetVariablesAsync(workflowRunId);
        var merged = VariableBundle.Patch(current, patch);
        return await SetVariablesAsync(workflowRunId, merged);
    }
}
