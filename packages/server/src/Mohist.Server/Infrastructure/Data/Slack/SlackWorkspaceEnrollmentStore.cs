using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Infrastructure.Data.Slack;

public sealed class SlackWorkspaceEnrollmentStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public SlackWorkspaceEnrollmentStore(IDbContextFactory<MohistDbContext> dbFactory, TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public async Task<SlackWorkspaceEnrollment?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackWorkspaceEnrollments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<SlackWorkspaceEnrollment?> GetActiveByTeamAsync(string workspaceTeamId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceTeamId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackWorkspaceEnrollments.AsNoTracking().SingleOrDefaultAsync(item =>
            item.WorkspaceTeamId == workspaceTeamId
            && item.Lifecycle == SlackEnrollmentLifecycle.Active
            && item.DeletedAt == null, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<SlackWorkspaceEnrollment> CreateAsync(SlackWorkspaceEnrollment enrollment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        if (string.IsNullOrWhiteSpace(enrollment.Id)) throw new ArgumentException("Enrollment id is required.", nameof(enrollment));
        if (string.IsNullOrWhiteSpace(enrollment.WorkspaceTeamId)) throw new ArgumentException("Workspace team id is required.", nameof(enrollment));
        if (string.IsNullOrWhiteSpace(enrollment.ManagerExternalId)) throw new ArgumentException("Manager external id is required.", nameof(enrollment));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        enrollment.CreatedAt = now;
        enrollment.UpdatedAt = now;
        db.SlackWorkspaceEnrollments.Add(ToRow(enrollment));
        await db.SaveChangesAsync(ct);
        return enrollment;
    }

    public async Task<SlackWorkspaceEnrollment?> UpdateAsync(
        string id,
        string? lifecycle = null,
        string? managerCapability = null,
        string? capabilityReason = null,
        DateTimeOffset? lastVerifiedAt = null,
        string? planCode = null,
        int? managedAppLimit = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackWorkspaceEnrollments.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (row is null) return null;
        if (lifecycle is not null) row.Lifecycle = lifecycle;
        if (managerCapability is not null) row.ManagerCapability = managerCapability;
        if (capabilityReason is not null) row.CapabilityReason = capabilityReason;
        if (lastVerifiedAt is not null) row.LastVerifiedAt = lastVerifiedAt;
        if (planCode is not null) row.PlanCode = planCode;
        if (managedAppLimit is not null) row.ManagedAppLimit = managedAppLimit.Value;
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDomain(row);
    }

    private static SlackWorkspaceEnrollment ToDomain(SlackWorkspaceEnrollmentRow row) => new()
    {
        Id = row.Id,
        WorkspaceTeamId = row.WorkspaceTeamId,
        ManagerExternalId = row.ManagerExternalId,
        Lifecycle = row.Lifecycle,
        ManagerCapability = row.ManagerCapability,
        CapabilityReason = row.CapabilityReason,
        LastVerifiedAt = row.LastVerifiedAt,
        PlanCode = row.PlanCode,
        ManagedAppLimit = row.ManagedAppLimit,
        ManagerCredentialRef = row.ManagerCredentialRef,
        AuditJson = row.AuditJson,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        DeletedAt = row.DeletedAt,
    };

    private static SlackWorkspaceEnrollmentRow ToRow(SlackWorkspaceEnrollment enrollment) => new()
    {
        Id = enrollment.Id,
        WorkspaceTeamId = enrollment.WorkspaceTeamId,
        ManagerExternalId = enrollment.ManagerExternalId,
        Lifecycle = enrollment.Lifecycle,
        ManagerCapability = enrollment.ManagerCapability,
        CapabilityReason = enrollment.CapabilityReason,
        LastVerifiedAt = enrollment.LastVerifiedAt,
        PlanCode = enrollment.PlanCode,
        ManagedAppLimit = enrollment.ManagedAppLimit,
        ManagerCredentialRef = enrollment.ManagerCredentialRef,
        AuditJson = enrollment.AuditJson,
        CreatedAt = enrollment.CreatedAt,
        UpdatedAt = enrollment.UpdatedAt,
        DeletedAt = enrollment.DeletedAt,
    };
}
