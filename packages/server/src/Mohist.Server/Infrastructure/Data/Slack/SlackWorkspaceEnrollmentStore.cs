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

    public async Task<SlackWorkspaceEnrollment?> GetByTeamAsync(string workspaceTeamId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceTeamId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackWorkspaceEnrollments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.WorkspaceTeamId == workspaceTeamId, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<SlackWorkspaceEnrollment> CreateAsync(SlackWorkspaceEnrollment enrollment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        if (string.IsNullOrWhiteSpace(enrollment.Id)) throw new ArgumentException("Enrollment id is required.", nameof(enrollment));
        if (string.IsNullOrWhiteSpace(enrollment.WorkspaceTeamId)) throw new ArgumentException("Workspace team id is required.", nameof(enrollment));
        if (enrollment.Lifecycle != SlackEnrollmentLifecycle.Active)
            throw new InvalidOperationException("A new workspace enrollment must start active.");
        SlackStateTransitions.RequireKnownManagerCapability(enrollment.ManagerCapability);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        enrollment.CreatedAt = now;
        enrollment.UpdatedAt = now;
        db.SlackWorkspaceEnrollments.Add(ToRow(enrollment));
        await db.SaveChangesAsync(ct);
        return enrollment;
    }

    public Task<SlackWorkspaceEnrollment?> TransitionLifecycleAsync(
        string id,
        string nextLifecycle,
        CancellationToken ct = default) =>
        UpdateAsync(id, enrollment => enrollment.TransitionLifecycle(nextLifecycle, _timeProvider.GetUtcNow()), ct);

    public Task<SlackWorkspaceEnrollment?> SetManagerCapabilityAsync(
        string id,
        string managerCapability,
        string? capabilityReason = null,
        DateTimeOffset? lastVerifiedAt = null,
        CancellationToken ct = default) =>
        UpdateAsync(id, enrollment => enrollment.SetManagerCapability(managerCapability, capabilityReason, lastVerifiedAt), ct);

    public Task<SlackWorkspaceEnrollment?> UpdatePlanAsync(
        string id,
        string planCode,
        int managedAppLimit,
        CancellationToken ct = default) =>
        UpdateAsync(id, enrollment => enrollment.UpdatePlan(planCode, managedAppLimit), ct);

    private async Task<SlackWorkspaceEnrollment?> UpdateAsync(
        string id,
        Action<SlackWorkspaceEnrollment> transition,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(transition);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackWorkspaceEnrollments.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (row is null) return null;
        var enrollment = ToDomain(row);
        transition(enrollment);
        Apply(enrollment, row);
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return enrollment;
    }

    private static SlackWorkspaceEnrollment ToDomain(SlackWorkspaceEnrollmentRow row) => new()
    {
        Id = row.Id,
        WorkspaceTeamId = row.WorkspaceTeamId,
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

    private static void Apply(SlackWorkspaceEnrollment enrollment, SlackWorkspaceEnrollmentRow row)
    {
        row.Lifecycle = enrollment.Lifecycle;
        row.ManagerCapability = enrollment.ManagerCapability;
        row.CapabilityReason = enrollment.CapabilityReason;
        row.LastVerifiedAt = enrollment.LastVerifiedAt;
        row.PlanCode = enrollment.PlanCode;
        row.ManagedAppLimit = enrollment.ManagedAppLimit;
        row.DeletedAt = enrollment.DeletedAt;
    }
}
