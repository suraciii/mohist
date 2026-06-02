using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Workflow.Scheduling;

public sealed class WorkflowScheduler : IWorkflowScheduler
{
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromMinutes(2);

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ILogger<WorkflowScheduler> _log;

    public WorkflowScheduler(IDbContextFactory<MohistDbContext> dbFactory, ILogger<WorkflowScheduler> log)
    {
        _dbFactory = dbFactory;
        _log = log;
    }

    public async Task EnqueueAsync(string workflowRunId, string projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var row = await db.WorkflowQueue.FindAsync([workflowRunId], cancellationToken);
        if (row is null)
        {
            db.WorkflowQueue.Add(new WorkflowQueueRow
            {
                WorkflowRunId = workflowRunId,
                ProjectId = projectId,
                UpdatedAt = now
            });
        }
        else if (row.State == WorkflowQueueStates.Queued)
        {
            row.ProjectId = projectId;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RequeueAsync(string workflowRunId, string projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.WorkflowQueue.FindAsync([workflowRunId], cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            db.WorkflowQueue.Add(new WorkflowQueueRow
            {
                WorkflowRunId = workflowRunId,
                ProjectId = projectId,
                UpdatedAt = now
            });
        }
        else
        {
            ResetToQueued(row, projectId, now);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearAsync(string workflowRunId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.WorkflowQueue.FindAsync([workflowRunId], cancellationToken);
        if (row is null) return;
        db.WorkflowQueue.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearQueuedAsync(string workflowRunId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.WorkflowQueue.FindAsync([workflowRunId], cancellationToken);
        if (row is null || row.State != WorkflowQueueStates.Queued) return;
        db.WorkflowQueue.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<WorkflowQueueRow?> GetAsync(string workflowRunId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.WorkflowQueue.AsNoTracking().FirstOrDefaultAsync(row => row.WorkflowRunId == workflowRunId, cancellationToken);
    }

    public async Task<WorkflowQueueRow?> ClaimAsync(string runnerId, IReadOnlyList<string> projectIds, int maxActiveLeases, CancellationToken cancellationToken = default)
    {
        if (maxActiveLeases <= 0)
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var expired = await ExpireLeasesAsync(db, now, cancellationToken);
        if (expired > 0)
            await db.SaveChangesAsync(cancellationToken);

        var active = await db.WorkflowQueue.CountAsync(
            row => row.State == WorkflowQueueStates.Leased
                && row.RunnerId == runnerId,
            cancellationToken);

        if (active >= maxActiveLeases)
            return null;

        var query = db.WorkflowQueue.Where(row => row.State == WorkflowQueueStates.Queued);
        if (projectIds.Count > 0)
            query = query.Where(row => projectIds.Contains(row.ProjectId));

        var row = (await query.ToListAsync(cancellationToken))
            .OrderBy(row => row.UpdatedAt)
            .FirstOrDefault();
        if (row is null)
            return null;

        row.State = WorkflowQueueStates.Leased;
        row.RunnerId = runnerId;
        row.WorkId = null;
        row.WorkType = null;
        row.Stage = null;
        row.LogicalId = null;
        row.Title = null;
        row.LeaseExpiresAt = now.Add(LeaseTtl);
        row.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _log.LogInformation("Runner {RunnerId} claimed workflow {WorkflowRunId} from workflow queue", runnerId, row.WorkflowRunId);
        return Detach(row);
    }

    public async Task AttachLeaseAsync(string workflowRunId, string projectId, string runnerId, WorkLease lease, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var row = await db.WorkflowQueue.FindAsync([workflowRunId], cancellationToken);
        if (row is null)
        {
            row = new WorkflowQueueRow { WorkflowRunId = workflowRunId, ProjectId = projectId };
            db.WorkflowQueue.Add(row);
        }
        else
        {
            row.ProjectId = projectId;
        }

        row.State = WorkflowQueueStates.Leased;
        row.RunnerId = runnerId;
        row.WorkId = lease.WorkId;
        row.WorkType = lease.WorkType;
        row.Stage = lease.Stage;
        row.LogicalId = lease.LogicalId;
        row.Title = lease.Title;
        row.LeaseExpiresAt = now.Add(LeaseTtl);
        row.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task HeartbeatRunnerAsync(string runnerId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var expiresAt = DateTimeOffset.UtcNow.Add(LeaseTtl);
        var updated = await db.WorkflowQueue
            .Where(row => row.State == WorkflowQueueStates.Leased && row.RunnerId == runnerId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.LeaseExpiresAt, expiresAt)
                .SetProperty(row => row.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken);
        if (updated > 0)
            _log.LogDebug("Extended {Count} workflow queue leases for runner {RunnerId}", updated, runnerId);
    }

    public async Task<int> ActiveLeaseCountAsync(string runnerId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await ExpireLeasesAsync(db, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await db.WorkflowQueue.CountAsync(
            row => row.State == WorkflowQueueStates.Leased
                && row.RunnerId == runnerId,
            cancellationToken);
    }

    public async Task<int> ExpireLeasesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var expired = await ExpireLeasesAsync(db, DateTimeOffset.UtcNow, cancellationToken);
        if (expired > 0)
            await db.SaveChangesAsync(cancellationToken);
        return expired;
    }

    private static async Task<int> ExpireLeasesAsync(MohistDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await db.WorkflowQueue
            .Where(row => row.State == WorkflowQueueStates.Leased)
            .ToListAsync(cancellationToken);

        var expiredRows = rows
            .Where(row => row.LeaseExpiresAt is not null && row.LeaseExpiresAt <= now)
            .ToList();

        foreach (var row in expiredRows)
            ResetToQueued(row, row.ProjectId, now);

        return expiredRows.Count;
    }

    private static void ResetToQueued(WorkflowQueueRow row, string projectId, DateTimeOffset now)
    {
        row.ProjectId = projectId;
        row.State = WorkflowQueueStates.Queued;
        row.RunnerId = null;
        row.WorkId = null;
        row.WorkType = null;
        row.Stage = null;
        row.LogicalId = null;
        row.Title = null;
        row.LeaseExpiresAt = null;
        row.UpdatedAt = now;
    }

    private static WorkflowQueueRow Detach(WorkflowQueueRow row)
    {
        return new WorkflowQueueRow
        {
            WorkflowRunId = row.WorkflowRunId,
            ProjectId = row.ProjectId,
            State = row.State,
            RunnerId = row.RunnerId,
            WorkId = row.WorkId,
            WorkType = row.WorkType,
            Stage = row.Stage,
            LogicalId = row.LogicalId,
            Title = row.Title,
            LeaseExpiresAt = row.LeaseExpiresAt,
            UpdatedAt = row.UpdatedAt
        };
    }
}
