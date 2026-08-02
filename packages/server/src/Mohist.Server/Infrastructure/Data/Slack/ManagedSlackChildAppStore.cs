using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Infrastructure.Data.Slack;

public sealed class ManagedSlackChildAppStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public ManagedSlackChildAppStore(IDbContextFactory<MohistDbContext> dbFactory, TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public async Task<ManagedSlackChildApp?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.ManagedSlackChildApps.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<ManagedSlackChildApp>> ListByEnrollmentAsync(string enrollmentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ManagedSlackChildApps.AsNoTracking()
            .Where(item => item.EnrollmentId == enrollmentId)
            .OrderBy(item => item.Id)
            .ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

    public async Task<ManagedSlackChildApp> CreateAsync(ManagedSlackChildApp childApp, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(childApp);
        if (string.IsNullOrWhiteSpace(childApp.Id)) throw new ArgumentException("Child App id is required.", nameof(childApp));
        if (string.IsNullOrWhiteSpace(childApp.EnrollmentId)) throw new ArgumentException("Enrollment id is required.", nameof(childApp));
        if (string.IsNullOrWhiteSpace(childApp.WorkspaceTeamId)) throw new ArgumentException("Workspace team id is required.", nameof(childApp));
        if (string.IsNullOrWhiteSpace(childApp.AgentConnectionId)) throw new ArgumentException("Agent connection id is required.", nameof(childApp));
        SlackStateTransitions.RequireChildAppLifecycleTransition(childApp.AppLifecycle, childApp.AppLifecycle);
        SlackStateTransitions.RequireAuthorizationTransition(childApp.Authorization, childApp.Authorization);
        SlackStateTransitions.RequireTransportKind(childApp.TransportKind);
        SlackStateTransitions.RequireBindingTransition(childApp.BindingState, childApp.BindingState);
        if (childApp.AppLifecycle != SlackAppLifecycle.NotCreated)
            throw new InvalidOperationException("A new Child App must start not_created.");
        if (childApp.Authorization != SlackAuthorizationState.NotStarted)
            throw new InvalidOperationException("A new Child App must start not_started.");
        if (childApp.BindingState != SlackChildAppBindingState.Pending)
            throw new InvalidOperationException("A new Child App must start with a pending binding.");
        if (!string.IsNullOrEmpty(childApp.AppId) || !string.IsNullOrEmpty(childApp.BotUserId))
            throw new InvalidOperationException("A new Child App cannot have a partially bound Slack identity.");
        if (childApp.DesiredManifestVersion <= 0 || string.IsNullOrWhiteSpace(childApp.DesiredManifestHash))
            throw new InvalidOperationException("A new Child App requires a versioned desired manifest.");
        if (childApp.AppliedManifestVersion is not null || !string.IsNullOrWhiteSpace(childApp.AppliedManifestHash))
            throw new InvalidOperationException("A new Child App cannot have an applied manifest.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var enrollmentExists = await db.SlackWorkspaceEnrollments.AnyAsync(item =>
            item.Id == childApp.EnrollmentId
            && item.WorkspaceTeamId == childApp.WorkspaceTeamId
            && item.DeletedAt == null, ct);
        if (!enrollmentExists) throw new InvalidOperationException("The Child App enrollment does not match an active workspace enrollment.");

        var now = _timeProvider.GetUtcNow();
        childApp.CreatedAt = now;
        childApp.UpdatedAt = now;
        db.ManagedSlackChildApps.Add(ToRow(childApp));
        await db.SaveChangesAsync(ct);
        return childApp;
    }

    public Task<ManagedSlackChildApp?> TransitionAppLifecycleAsync(
        string id,
        string nextLifecycle,
        CancellationToken ct = default) =>
        UpdateAsync(id, childApp => childApp.TransitionAppLifecycle(nextLifecycle), ct);

    public Task<ManagedSlackChildApp?> TransitionAuthorizationAsync(
        string id,
        string nextAuthorization,
        CancellationToken ct = default) =>
        UpdateAsync(id, childApp => childApp.TransitionAuthorization(nextAuthorization), ct);

    public Task<ManagedSlackChildApp?> SetTransportKindAsync(
        string id,
        string transportKind,
        CancellationToken ct = default) =>
        UpdateAsync(id, childApp => childApp.SetTransportKind(transportKind), ct);

    public Task<ManagedSlackChildApp?> TransitionBindingStateAsync(
        string id,
        string nextBindingState,
        CancellationToken ct = default) =>
        UpdateAsync(id, childApp => childApp.TransitionBindingState(nextBindingState), ct);

    private async Task<ManagedSlackChildApp?> UpdateAsync(
        string id,
        Action<ManagedSlackChildApp> transition,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(transition);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.ManagedSlackChildApps.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (row is null) return null;
        var childApp = ToDomain(row);
        transition(childApp);
        Apply(childApp, row);
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return childApp;
    }

    private static ManagedSlackChildApp ToDomain(ManagedSlackChildAppRow row) => new()
    {
        Id = row.Id,
        EnrollmentId = row.EnrollmentId,
        WorkspaceTeamId = row.WorkspaceTeamId,
        AgentConnectionId = row.AgentConnectionId,
        PublicIngressBaseUrl = row.PublicIngressBaseUrl,
        AppId = row.AppId,
        BotUserId = row.BotUserId,
        AppLifecycle = row.AppLifecycle,
        Authorization = row.Authorization,
        TransportKind = row.TransportKind,
        DesiredManifestVersion = row.DesiredManifestVersion,
        DesiredManifestHash = row.DesiredManifestHash,
        AppliedManifestVersion = row.AppliedManifestVersion,
        AppliedManifestHash = row.AppliedManifestHash,
        VerifiedScopesJson = row.VerifiedScopesJson,
        OperationFence = row.OperationFence,
        OperationId = row.OperationId,
        OperationKind = row.OperationKind,
        OperationStartedAt = row.OperationStartedAt,
        UnknownOutcome = row.UnknownOutcome,
        ErrorClass = row.ErrorClass,
        AuthorizationAttemptId = row.AuthorizationAttemptId,
        AuthorizedAt = row.AuthorizedAt,
        AuthorizationExpiresAt = row.AuthorizationExpiresAt,
        ClientSecretRef = row.ClientSecretRef,
        SigningSecretRef = row.SigningSecretRef,
        AppLevelTokenRef = row.AppLevelTokenRef,
        BotTokenRef = row.BotTokenRef,
        BindingState = row.BindingState,
        BindingErrorClass = row.BindingErrorClass,
        AuditJson = row.AuditJson,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        DeletedAt = row.DeletedAt,
    };

    private static ManagedSlackChildAppRow ToRow(ManagedSlackChildApp childApp) => new()
    {
        Id = childApp.Id,
        EnrollmentId = childApp.EnrollmentId,
        WorkspaceTeamId = childApp.WorkspaceTeamId,
        AgentConnectionId = childApp.AgentConnectionId,
        PublicIngressBaseUrl = childApp.PublicIngressBaseUrl,
        AppId = childApp.AppId,
        BotUserId = childApp.BotUserId,
        AppLifecycle = childApp.AppLifecycle,
        Authorization = childApp.Authorization,
        TransportKind = childApp.TransportKind,
        DesiredManifestVersion = childApp.DesiredManifestVersion,
        DesiredManifestHash = childApp.DesiredManifestHash,
        AppliedManifestVersion = childApp.AppliedManifestVersion,
        AppliedManifestHash = childApp.AppliedManifestHash,
        VerifiedScopesJson = childApp.VerifiedScopesJson,
        OperationFence = childApp.OperationFence,
        OperationId = childApp.OperationId,
        OperationKind = childApp.OperationKind,
        OperationStartedAt = childApp.OperationStartedAt,
        UnknownOutcome = childApp.UnknownOutcome,
        ErrorClass = childApp.ErrorClass,
        AuthorizationAttemptId = childApp.AuthorizationAttemptId,
        AuthorizedAt = childApp.AuthorizedAt,
        AuthorizationExpiresAt = childApp.AuthorizationExpiresAt,
        ClientSecretRef = childApp.ClientSecretRef,
        SigningSecretRef = childApp.SigningSecretRef,
        AppLevelTokenRef = childApp.AppLevelTokenRef,
        BotTokenRef = childApp.BotTokenRef,
        BindingState = childApp.BindingState,
        BindingErrorClass = childApp.BindingErrorClass,
        AuditJson = childApp.AuditJson,
        CreatedAt = childApp.CreatedAt,
        UpdatedAt = childApp.UpdatedAt,
        DeletedAt = childApp.DeletedAt,
    };
    private static void Apply(ManagedSlackChildApp childApp, ManagedSlackChildAppRow row)
    {
        row.AppLifecycle = childApp.AppLifecycle;
        row.Authorization = childApp.Authorization;
        row.TransportKind = childApp.TransportKind;
        row.BindingState = childApp.BindingState;
        row.DeletedAt = childApp.DeletedAt;
    }

}
