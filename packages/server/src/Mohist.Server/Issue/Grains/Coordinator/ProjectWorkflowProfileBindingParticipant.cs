using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Grains.Coordinator;

/// <summary>
/// issue-477 T-001: Project-side participant that commits the Project
/// default WorkflowProfile binding. The backing key is written only when
/// the Profile is a custom (non-built-in) row; built-in bindings leave
/// the backing key null.
///
/// The participant is not a coordinator — it never holds a fence. It
/// depends on the
/// <c>WorkflowProfileReferenceCoordinator</c>'s captured expected
/// revision to detect duplicate replays.
/// </summary>
public sealed class ProjectWorkflowProfileBindingParticipant : Grain, IProjectWorkflowProfileBindingParticipant
{
    private readonly IGrainFactory _grains;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public ProjectWorkflowProfileBindingParticipant(
        IGrainFactory grains,
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider? timeProvider = null)
    {
        _grains = grains;
        _dbFactory = dbFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProjectWorkflowProfileBindingOutcome> SetDefaultAsync(
        WorkflowProfileCommandPayload.SetProjectDefault payload,
        string commandId,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(payload);

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
