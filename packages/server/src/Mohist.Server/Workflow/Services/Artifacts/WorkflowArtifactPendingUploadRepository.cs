using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;

namespace Mohist.Server.Workflow.Services.Artifacts;

internal sealed class WorkflowArtifactPendingUploadRepository
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowArtifactPendingUploadRepository(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<WorkflowArtifactPendingUploadRow?> FindByKeyAsync(
        WorkflowArtifactPendingUploadKey key,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.WorkflowArtifactPendingUploads
            .AsNoTracking()
            .FirstOrDefaultAsync(p =>
                p.WorkflowRunId == key.WorkflowRunId
                && p.WorkId == key.WorkId
                && p.ActionAttemptId == key.ActionAttemptId
                && p.Path == key.Path,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PendingArtifactUploadCommitResult> TryCreateAsync(
        WorkflowArtifactPendingUploadRow pending,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.WorkflowArtifactPendingUploads.Add(pending);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return PendingArtifactUploadCommitResult.Created(pending);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            db.ChangeTracker.Clear();
            var existing = await db.WorkflowArtifactPendingUploads
                .AsNoTracking()
                .FirstOrDefaultAsync(p =>
                    p.WorkflowRunId == pending.WorkflowRunId
                    && p.WorkId == pending.WorkId
                    && p.ActionAttemptId == pending.ActionAttemptId
                    && p.Path == pending.Path,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null) throw;
            return PendingArtifactUploadCommitResult.Existing(existing);
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("constraint", StringComparison.OrdinalIgnoreCase) == true;
}

internal sealed record WorkflowArtifactPendingUploadKey(
    string WorkflowRunId,
    string WorkId,
    string ActionAttemptId,
    string Path);

internal sealed record PendingArtifactUploadCommitResult(
    bool WasCreated,
    WorkflowArtifactPendingUploadRow Row)
{
    public static PendingArtifactUploadCommitResult Created(WorkflowArtifactPendingUploadRow row) =>
        new(true, row);

    public static PendingArtifactUploadCommitResult Existing(WorkflowArtifactPendingUploadRow row) =>
        new(false, row);
}
