using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;

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
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowRunProfiles
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);
        var pullRequestNumber = WorkflowRunExtensions.ReadPullRequestNumber(bundle.Vars);
        var workflowRunRow = pullRequestNumber is null
            ? null
            : await db.WorkflowRuns
                .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);

        if (workflowRunRow is not null && pullRequestNumber is not null)
        {
            var run = JSON.Deserialize<WorkflowRun>(workflowRunRow.State)
                ?? throw new InvalidOperationException(
                    $"WorkflowRun '{workflowRunId}' has unreadable state");
            run.ValidatePullRequestNumber(pullRequestNumber.Value);
        }

        if (row is null)
        {
            row = new WorkflowRunProfileRow
            {
                WorkflowRunId = workflowRunId,
                Variables = bundle.ToJson(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.WorkflowRunProfiles.Add(row);
            db.Entry(row).Property<long>("ETag").CurrentValue = 1;
        }
        else
        {
            row.Variables = bundle.ToJson();
            row.UpdatedAt = DateTimeOffset.UtcNow;
            BumpETag(db.Entry(row));
        }

        await db.SaveChangesAsync();
        return bundle;
    }

    public async Task<VariableBundle> PatchVariablesAsync(string workflowRunId, VariableBundle patch)
    {
        VariableBundleShapeValidator.Validate(patch);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowRunProfiles
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);
        var workflowRunRow = await db.WorkflowRuns
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);
        var desiredExplicit = VariableBundle.Patch(
            row is null ? VariableBundle.Empty : VariableBundle.FromJson(row.Variables),
            patch);

        if (workflowRunRow is not null)
        {
            var pullRequestNumber = WorkflowRunExtensions.ReadPullRequestNumber(desiredExplicit.Vars);
            if (pullRequestNumber is not null)
            {
                var run = JSON.Deserialize<WorkflowRun>(workflowRunRow.State)
                    ?? throw new InvalidOperationException(
                        $"WorkflowRun '{workflowRunId}' has unreadable state");
                run.AssignPullRequestIdentity(pullRequestNumber.Value);
                workflowRunRow.State = JSON.Serialize(run);
                workflowRunRow.PullRequestNumber = run.PullRequestIdentity?.Number;
                BumpETag(db.Entry(workflowRunRow));
            }
        }

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

    private static void BumpETag<TEntity>(EntityEntry<TEntity> entry)
        where TEntity : class
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
