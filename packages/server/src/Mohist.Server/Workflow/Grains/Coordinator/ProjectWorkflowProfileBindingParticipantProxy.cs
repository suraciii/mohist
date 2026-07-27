using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Grains.Coordinator;

/// <summary>
/// Project-side participant that commits the Project
/// default WorkflowProfile binding. The backing key is written only when
/// the Profile is a custom (non-built-in) row; built-in bindings leave
/// the backing key null.
///
/// The participant is not a coordinator — it never holds a fence. It
/// depends on the
/// <c>WorkflowProfileReferenceCoordinator</c>'s captured expected
/// revision to detect duplicate replays.
/// </summary>
public sealed class ProjectWorkflowProfileBindingParticipantProxy : Grain, IProjectWorkflowProfileBindingParticipant
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkflowProfileProvider _profileProvider;

    public ProjectWorkflowProfileBindingParticipantProxy(
        IDbContextFactory<MohistDbContext> dbFactory,
        IWorkflowProfileProvider profileProvider,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _profileProvider = profileProvider;
    }

    public async Task<ProjectWorkflowProfileBindingOutcome> SetDefaultAsync(
        WorkflowProfileCommandPayload.SetProjectDefault payload,
        string commandId,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // Revalidate collection membership at the participant boundary: the
        // coordinator's earlier probe is not this transaction's validation,
        // and a stale/direct replay must not accept a Profile deleted between
        // the probe and the write.
        var exists = await _profileProvider.GetAsync(payload.ProjectId, payload.ProfileId);
        if (exists is null)
            return ProjectWorkflowProfileBindingOutcome.ProfileUnknown;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectWorkflowProfiles
            .FirstOrDefaultAsync(r => r.ProjectId == payload.ProjectId);
        if (row is null)
            return ProjectWorkflowProfileBindingOutcome.ProjectNotFound;

        if (string.Equals(row.DefaultWorkflowProfileId, payload.ProfileId, StringComparison.Ordinal))
            return ProjectWorkflowProfileBindingOutcome.AlreadyApplied;

        row.DefaultWorkflowProfileId = payload.ProfileId;
        row.DefaultWorkflowProfileIdKey = IsBuiltInProfile(payload.ProfileId)
            ? null
            : payload.ProfileId;
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync();
        return ProjectWorkflowProfileBindingOutcome.Applied;
    }

    public async Task<long> GetWorkflowProfileBindingRevisionAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == this.GetPrimaryKeyString());
        return row is null ? 0L : 1L;
    }

    private static bool IsBuiltInProfile(string profileId) =>
        WorkflowProfileCatalog.IsSystemProfile(profileId);
}
