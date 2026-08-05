using System.Security.Cryptography;
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

    public Task<SlackWorkspaceEnrollment?> ConfigureManagerAppAsync(
        string id,
        string appId,
        string botUserId,
        string credentialRef,
        string transportKind,
        string readiness,
        CancellationToken ct = default) =>
        UpdateAsync(id, enrollment => enrollment.ConfigureManagerApp(
            appId,
            botUserId,
            credentialRef,
            transportKind,
            readiness,
            _timeProvider.GetUtcNow()), ct);

    public Task<SlackWorkspaceEnrollment?> EnsureManagerActorAsync(
        string id,
        string actorId,
        CancellationToken ct = default) =>
        UpdateAsync(id, enrollment => enrollment.EnsureManagerActor(actorId, _timeProvider.GetUtcNow()), ct);

    public async Task<SlackManagerClaimIssuance> IssueManagerClaimAsync(
        string id,
        string claimHash,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimHash);
        if (expiresAt <= issuedAt)
            throw new ArgumentOutOfRangeException(nameof(expiresAt));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackWorkspaceEnrollments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, ct);
        if (row is null)
            return SlackManagerClaimIssuance.NotFound;

        var enrollment = ToDomain(row);
        if (enrollment.Lifecycle != SlackEnrollmentLifecycle.Active
            || !string.IsNullOrWhiteSpace(enrollment.ClaimedSlackUserId))
            return new(enrollment, false);

        enrollment.ManagerClaimHash = claimHash;
        enrollment.ManagerClaimIssuedAt = issuedAt;
        enrollment.ManagerClaimExpiresAt = expiresAt;
        enrollment.ManagerClaimConsumedAt = null;
        enrollment.AppendAudit("manager_claim_issued", null, issuedAt);

        var expectedHash = row.ManagerClaimHash;
        var updated = await db.SlackWorkspaceEnrollments
            .Where(item => item.Id == id
                && item.Lifecycle == SlackEnrollmentLifecycle.Active
                && item.ClaimedSlackUserId == null
                && item.ManagerClaimConsumedAt == null
                && item.ManagerClaimHash == expectedHash)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ManagerClaimHash, enrollment.ManagerClaimHash)
                .SetProperty(item => item.ManagerClaimIssuedAt, enrollment.ManagerClaimIssuedAt)
                .SetProperty(item => item.ManagerClaimExpiresAt, enrollment.ManagerClaimExpiresAt)
                .SetProperty(item => item.ManagerClaimConsumedAt, enrollment.ManagerClaimConsumedAt)
                .SetProperty(item => item.AuditJson, enrollment.AuditJson)
                .SetProperty(item => item.UpdatedAt, issuedAt), ct);
        if (updated == 1)
            return new(enrollment, true);

        var current = await db.SlackWorkspaceEnrollments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, ct);
        return current is null
            ? SlackManagerClaimIssuance.NotFound
            : new(ToDomain(current), false);
    }

    public async Task<SlackManagerClaimConsumption> ConsumeManagerClaimAsync(
        string id,
        string workspaceTeamId,
        string slackUserId,
        string claimHash,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceTeamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slackUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimHash);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackWorkspaceEnrollments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, ct);
        if (row is null || !string.Equals(row.WorkspaceTeamId, workspaceTeamId, StringComparison.Ordinal))
            return new(SlackManagerClaimOutcome.Rejected);

        var enrollment = ToDomain(row);
        if (enrollment.Lifecycle != SlackEnrollmentLifecycle.Active)
            return new(SlackManagerClaimOutcome.Rejected);
        if (enrollment.ManagerClaimConsumedAt is not null)
            return new(SlackManagerClaimOutcome.Consumed);
        if (string.IsNullOrWhiteSpace(enrollment.ManagerClaimHash))
            return new(SlackManagerClaimOutcome.NoClaim);
        if (enrollment.ManagerClaimExpiresAt is null || enrollment.ManagerClaimExpiresAt <= now)
            return new(SlackManagerClaimOutcome.Expired);

        byte[] expected;
        byte[] supplied;
        try
        {
            expected = Convert.FromHexString(enrollment.ManagerClaimHash);
            supplied = Convert.FromHexString(claimHash);
        }
        catch (FormatException)
        {
            return new(SlackManagerClaimOutcome.Invalid);
        }
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
            return new(SlackManagerClaimOutcome.Invalid);

        enrollment.BindManagerActor(slackUserId, now);
        var updated = await db.SlackWorkspaceEnrollments
            .Where(item => item.Id == id
                && item.WorkspaceTeamId == workspaceTeamId
                && item.Lifecycle == SlackEnrollmentLifecycle.Active
                && item.ClaimedSlackUserId == null
                && item.ManagerClaimConsumedAt == null
                && item.ManagerClaimHash == claimHash
                && item.ManagerClaimExpiresAt == enrollment.ManagerClaimExpiresAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ClaimedSlackUserId, enrollment.ClaimedSlackUserId)
                .SetProperty(item => item.ManagerClaimHash, enrollment.ManagerClaimHash)
                .SetProperty(item => item.ManagerClaimConsumedAt, enrollment.ManagerClaimConsumedAt)
                .SetProperty(item => item.AuditJson, enrollment.AuditJson)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (updated != 1)
        {
            var current = await db.SlackWorkspaceEnrollments.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id, ct);
            if (current is null || !string.Equals(current.WorkspaceTeamId, workspaceTeamId, StringComparison.Ordinal))
                return new(SlackManagerClaimOutcome.Rejected);
            if (current.ManagerClaimConsumedAt is not null)
                return new(SlackManagerClaimOutcome.Consumed);
            if (string.IsNullOrWhiteSpace(current.ManagerClaimHash))
                return new(SlackManagerClaimOutcome.NoClaim);
            if (current.ManagerClaimExpiresAt is null || current.ManagerClaimExpiresAt <= now)
                return new(SlackManagerClaimOutcome.Expired);
            return new(SlackManagerClaimOutcome.Rejected);
        }
        return new(
            SlackManagerClaimOutcome.Accepted,
            enrollment.Id,
            enrollment.WorkspaceTeamId,
            enrollment.ManagerActorId,
            enrollment.ClaimedSlackUserId);
    }

    public sealed record SlackManagerClaimIssuance(
        SlackWorkspaceEnrollment? Enrollment,
        bool Issued)
    {
        public static SlackManagerClaimIssuance NotFound { get; } = new(null, false);
    }

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
        ConfigurationCredentialRef = row.ConfigurationCredentialRef,
        ConfigurationCredentialGeneration = row.ConfigurationCredentialGeneration,
        ConfigurationCredentialExpiresAt = row.ConfigurationCredentialExpiresAt,
        ManagerCredentialRef = row.ManagerCredentialRef,
        ManagerAppId = row.ManagerAppId,
        ManagerBotUserId = row.ManagerBotUserId,
        ManagerTransportKind = row.ManagerTransportKind,
        ManagerReadiness = row.ManagerReadiness,
        ManagerActorId = row.ManagerActorId,
        ClaimedSlackUserId = row.ClaimedSlackUserId,
        ManagerClaimHash = row.ManagerClaimHash,
        ManagerClaimIssuedAt = row.ManagerClaimIssuedAt,
        ManagerClaimExpiresAt = row.ManagerClaimExpiresAt,
        ManagerClaimConsumedAt = row.ManagerClaimConsumedAt,
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
        ConfigurationCredentialRef = enrollment.ConfigurationCredentialRef,
        ConfigurationCredentialGeneration = enrollment.ConfigurationCredentialGeneration,
        ConfigurationCredentialExpiresAt = enrollment.ConfigurationCredentialExpiresAt,
        ManagerCredentialRef = enrollment.ManagerCredentialRef,
        ManagerAppId = enrollment.ManagerAppId,
        ManagerBotUserId = enrollment.ManagerBotUserId,
        ManagerTransportKind = enrollment.ManagerTransportKind,
        ManagerReadiness = enrollment.ManagerReadiness,
        ManagerActorId = enrollment.ManagerActorId,
        ClaimedSlackUserId = enrollment.ClaimedSlackUserId,
        ManagerClaimHash = enrollment.ManagerClaimHash,
        ManagerClaimIssuedAt = enrollment.ManagerClaimIssuedAt,
        ManagerClaimExpiresAt = enrollment.ManagerClaimExpiresAt,
        ManagerClaimConsumedAt = enrollment.ManagerClaimConsumedAt,
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
        row.ConfigurationCredentialRef = enrollment.ConfigurationCredentialRef;
        row.ConfigurationCredentialGeneration = enrollment.ConfigurationCredentialGeneration;
        row.ConfigurationCredentialExpiresAt = enrollment.ConfigurationCredentialExpiresAt;
        row.ManagerCredentialRef = enrollment.ManagerCredentialRef;
        row.ManagerAppId = enrollment.ManagerAppId;
        row.ManagerBotUserId = enrollment.ManagerBotUserId;
        row.ManagerTransportKind = enrollment.ManagerTransportKind;
        row.ManagerReadiness = enrollment.ManagerReadiness;
        row.ManagerActorId = enrollment.ManagerActorId;
        row.ClaimedSlackUserId = enrollment.ClaimedSlackUserId;
        row.ManagerClaimHash = enrollment.ManagerClaimHash;
        row.ManagerClaimIssuedAt = enrollment.ManagerClaimIssuedAt;
        row.ManagerClaimExpiresAt = enrollment.ManagerClaimExpiresAt;
        row.ManagerClaimConsumedAt = enrollment.ManagerClaimConsumedAt;
        row.AuditJson = enrollment.AuditJson;
        row.DeletedAt = enrollment.DeletedAt;
    }
}
