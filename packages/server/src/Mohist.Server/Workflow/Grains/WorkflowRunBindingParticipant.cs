using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// issue-477 T-001: WorkflowRun-side participant that commits the
/// Profile ID binding. The backing key is written only when the Profile
/// is a custom (non-built-in) row; built-in bindings leave the backing
/// key null. Terminalization calls <see cref="ClearBindingAsync"/> to
/// drop the backing key while retaining the public Profile ID.
/// </summary>
public sealed class WorkflowRunBindingParticipant : Grain, IWorkflowRunBindingParticipant
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowRunBindingParticipant(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<WorkflowRunBindingOutcome> BindAsync(
        WorkflowProfileCommandPayload.BindWorkflowRun payload,
        string commandId,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(payload);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowRuns
            .FirstOrDefaultAsync(r => r.WorkflowRunId == payload.WorkflowRunId);
        if (row is null)
            return WorkflowRunBindingOutcome.RunNotFound;

        if (string.Equals(row.WorkflowProfileIdKey, payload.ProfileId, StringComparison.Ordinal))
            return WorkflowRunBindingOutcome.AlreadyApplied;

        row.WorkflowProfileIdKey = IsBuiltInProfile(payload.ProfileId)
            ? null
            : payload.ProfileId;
        await db.SaveChangesAsync();
        return WorkflowRunBindingOutcome.Applied;
    }

    public async Task<WorkflowRunBindingOutcome> ClearBindingAsync(
        WorkflowProfileCommandPayload.BindWorkflowRun payload,
        string commandId,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(payload);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowRuns
            .FirstOrDefaultAsync(r => r.WorkflowRunId == payload.WorkflowRunId);
        if (row is null)
            return WorkflowRunBindingOutcome.RunNotFound;

        if (row.WorkflowProfileIdKey is null)
            return WorkflowRunBindingOutcome.AlreadyApplied;

        row.WorkflowProfileIdKey = null;
        await db.SaveChangesAsync();
        return WorkflowRunBindingOutcome.Applied;
    }

    private static bool IsBuiltInProfile(string profileId) =>
        WorkflowProfileCatalog.IsSystemProfile(profileId);
}
