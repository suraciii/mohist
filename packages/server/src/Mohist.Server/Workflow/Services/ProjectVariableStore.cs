using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Services;

public class ProjectVariableStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public ProjectVariableStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<VariableBundle> GetVariablesAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);
        return row is null ? VariableBundle.Empty : VariableBundle.FromJson(row.Variables);
    }

    public async Task<VariableBundle> SetVariablesAsync(string projectId, VariableBundle bundle)
    {
        VariableBundleShapeValidator.Validate(bundle);
        var sanitized = ProjectVariablesFilter.Sanitize(bundle);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectWorkflowProfiles
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);

        if (row is null)
        {
            row = new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                Variables = sanitized.ToJson(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.ProjectWorkflowProfiles.Add(row);
        }
        else
        {
            row.Variables = sanitized.ToJson();
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return sanitized;
    }

    public async Task<VariableBundle> PatchVariablesAsync(string projectId, VariableBundle patch)
    {
        VariableBundleShapeValidator.Validate(patch);
        var current = await GetVariablesAsync(projectId);
        var merged = VariableBundle.Patch(current, patch);
        return await SetVariablesAsync(projectId, merged);
    }
}
