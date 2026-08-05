using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Infrastructure.Data.Slack;

public sealed class ManagedSlackAgentAppStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public ManagedSlackAgentAppStore(IDbContextFactory<MohistDbContext> dbFactory, TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public async Task<ManagedSlackAgentApp?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.ManagedSlackAgentApps.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<ManagedSlackAgentApp>> ListByEnrollmentAsync(string enrollmentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ManagedSlackAgentApps.AsNoTracking()
            .Where(item => item.EnrollmentId == enrollmentId)
            .OrderBy(item => item.Id)
            .ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

    public async Task<ManagedSlackAgentApp?> GetByConnectionAsync(string connectionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.ManagedSlackAgentApps.AsNoTracking()
            .SingleOrDefaultAsync(item => item.AgentConnectionId == connectionId && item.DeletedAt == null, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<bool> HasUndeletedForAgentAndWorkspaceAsync(
        string projectId,
        string agentId,
        string workspaceTeamId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceTeamId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ManagedSlackAgentApps.AnyAsync(agentApp =>
            agentApp.DeletedAt == null
            && agentApp.WorkspaceTeamId == workspaceTeamId
            && db.AgentConnections.Any(connection =>
                connection.Id == agentApp.AgentConnectionId
                && connection.ProjectId == projectId
                && connection.AgentId == agentId), ct);
    }

    public async Task<ManagedSlackAgentApp> CreateAsync(ManagedSlackAgentApp agentApp, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentApp);
        if (string.IsNullOrWhiteSpace(agentApp.Id)) throw new ArgumentException("Agent App id is required.", nameof(agentApp));
        if (string.IsNullOrWhiteSpace(agentApp.EnrollmentId)) throw new ArgumentException("Enrollment id is required.", nameof(agentApp));
        if (string.IsNullOrWhiteSpace(agentApp.WorkspaceTeamId)) throw new ArgumentException("Workspace team id is required.", nameof(agentApp));
        if (string.IsNullOrWhiteSpace(agentApp.AgentConnectionId)) throw new ArgumentException("Agent connection id is required.", nameof(agentApp));
        SlackStateTransitions.RequireAgentAppLifecycleTransition(agentApp.AppLifecycle, agentApp.AppLifecycle);
        SlackStateTransitions.RequireAuthorizationTransition(agentApp.Authorization, agentApp.Authorization);
        SlackStateTransitions.RequireBindingTransition(agentApp.BindingState, agentApp.BindingState);
        if (agentApp.AppLifecycle != SlackAppLifecycle.NotCreated)
            throw new InvalidOperationException("A new Agent App must start not_created.");
        if (agentApp.Authorization != SlackAuthorizationState.NotStarted)
            throw new InvalidOperationException("A new Agent App must start not_started.");
        if (agentApp.BindingState != SlackAgentAppBindingState.Pending)
            throw new InvalidOperationException("A new Agent App must start with a pending binding.");
        if (!string.IsNullOrEmpty(agentApp.AppId) || !string.IsNullOrEmpty(agentApp.BotUserId))
            throw new InvalidOperationException("A new Agent App cannot have a partially bound Slack identity.");
        if (agentApp.DesiredManifestVersion <= 0 || string.IsNullOrWhiteSpace(agentApp.DesiredManifestHash))
            throw new InvalidOperationException("A new Agent App requires a versioned desired manifest.");
        if (agentApp.AppliedManifestVersion is not null || !string.IsNullOrWhiteSpace(agentApp.AppliedManifestHash))
            throw new InvalidOperationException("A new Agent App cannot have an applied manifest.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var enrollmentExists = await db.SlackWorkspaceEnrollments.AnyAsync(item =>
            item.Id == agentApp.EnrollmentId
            && item.WorkspaceTeamId == agentApp.WorkspaceTeamId
            && item.DeletedAt == null, ct);
        if (!enrollmentExists) throw new InvalidOperationException("The Agent App enrollment does not match an active workspace enrollment.");

        var now = _timeProvider.GetUtcNow();
        agentApp.CreatedAt = now;
        agentApp.UpdatedAt = now;
        db.ManagedSlackAgentApps.Add(ToRow(agentApp));
        await db.SaveChangesAsync(ct);
        return agentApp;
    }

    public Task<ManagedSlackAgentApp?> TransitionAppLifecycleAsync(
        string id,
        string nextLifecycle,
        CancellationToken ct = default) =>
        UpdateAsync(id, agentApp => agentApp.TransitionAppLifecycle(nextLifecycle), ct);

    public Task<ManagedSlackAgentApp?> TransitionAuthorizationAsync(
        string id,
        string nextAuthorization,
        CancellationToken ct = default) =>
        UpdateAsync(id, agentApp => agentApp.TransitionAuthorization(nextAuthorization), ct);

    public Task<ManagedSlackAgentApp?> TransitionBindingStateAsync(
        string id,
        string nextBindingState,
        CancellationToken ct = default) =>
        UpdateAsync(id, agentApp => agentApp.TransitionBindingState(nextBindingState), ct);

    public Task<ManagedSlackAgentApp?> UpdateDesiredManifestAsync(
        string id,
        int manifestVersion,
        string manifestHash,
        CancellationToken ct = default) =>
        UpdateAsync(id, agentApp =>
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(manifestVersion);
            ArgumentException.ThrowIfNullOrWhiteSpace(manifestHash);
            agentApp.DesiredManifestVersion = manifestVersion;
            agentApp.DesiredManifestHash = manifestHash;
        }, ct);

    public Task<ManagedSlackAgentApp?> StageRuntimeCredentialsAsync(
        string id,
        string botTokenRef,
        string appLevelTokenRef,
        string botUserId,
        string verifiedScopesJson,
        CancellationToken ct = default) =>
        UpdateAsync(id, agentApp => agentApp.StageRuntimeCredentials(
            botTokenRef, appLevelTokenRef, botUserId, verifiedScopesJson), ct);

    public Task<ManagedSlackAgentApp?> ApplyCredentialValidationAsync(
        string id,
        string validationState,
        CancellationToken ct = default) =>
        UpdateAsync(id, agentApp => agentApp.ApplyCredentialValidation(validationState), ct);

    private async Task<ManagedSlackAgentApp?> UpdateAsync(
        string id,
        Action<ManagedSlackAgentApp> transition,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(transition);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.ManagedSlackAgentApps.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (row is null) return null;
        var agentApp = ToDomain(row);
        transition(agentApp);
        Apply(agentApp, row);
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return agentApp;
    }

    private static ManagedSlackAgentApp ToDomain(ManagedSlackAgentAppRow row) => new()
    {
        Id = row.Id,
        EnrollmentId = row.EnrollmentId,
        WorkspaceTeamId = row.WorkspaceTeamId,
        AgentConnectionId = row.AgentConnectionId,
        AppId = row.AppId,
        BotUserId = row.BotUserId,
        AppLifecycle = row.AppLifecycle,
        Authorization = row.Authorization,
        DesiredManifestVersion = row.DesiredManifestVersion,
        DesiredManifestHash = row.DesiredManifestHash,
        AppliedManifestVersion = row.AppliedManifestVersion,
        AppliedManifestHash = row.AppliedManifestHash,
        VerifiedScopesJson = row.VerifiedScopesJson,
        InstallUrl = row.InstallUrl,
        RuntimeCredentialValidationState = row.RuntimeCredentialValidationState,
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

    private static ManagedSlackAgentAppRow ToRow(ManagedSlackAgentApp agentApp) => new()
    {
        Id = agentApp.Id,
        EnrollmentId = agentApp.EnrollmentId,
        WorkspaceTeamId = agentApp.WorkspaceTeamId,
        AgentConnectionId = agentApp.AgentConnectionId,
        AppId = agentApp.AppId,
        BotUserId = agentApp.BotUserId,
        AppLifecycle = agentApp.AppLifecycle,
        Authorization = agentApp.Authorization,
        DesiredManifestVersion = agentApp.DesiredManifestVersion,
        DesiredManifestHash = agentApp.DesiredManifestHash,
        AppliedManifestVersion = agentApp.AppliedManifestVersion,
        AppliedManifestHash = agentApp.AppliedManifestHash,
        VerifiedScopesJson = agentApp.VerifiedScopesJson,
        InstallUrl = agentApp.InstallUrl,
        RuntimeCredentialValidationState = agentApp.RuntimeCredentialValidationState,
        OperationFence = agentApp.OperationFence,
        OperationId = agentApp.OperationId,
        OperationKind = agentApp.OperationKind,
        OperationStartedAt = agentApp.OperationStartedAt,
        UnknownOutcome = agentApp.UnknownOutcome,
        ErrorClass = agentApp.ErrorClass,
        AuthorizationAttemptId = agentApp.AuthorizationAttemptId,
        AuthorizedAt = agentApp.AuthorizedAt,
        AuthorizationExpiresAt = agentApp.AuthorizationExpiresAt,
        ClientSecretRef = agentApp.ClientSecretRef,
        SigningSecretRef = agentApp.SigningSecretRef,
        AppLevelTokenRef = agentApp.AppLevelTokenRef,
        BotTokenRef = agentApp.BotTokenRef,
        BindingState = agentApp.BindingState,
        BindingErrorClass = agentApp.BindingErrorClass,
        AuditJson = agentApp.AuditJson,
        CreatedAt = agentApp.CreatedAt,
        UpdatedAt = agentApp.UpdatedAt,
        DeletedAt = agentApp.DeletedAt,
    };
    private static void Apply(ManagedSlackAgentApp agentApp, ManagedSlackAgentAppRow row)
    {
        row.AppLifecycle = agentApp.AppLifecycle;
        row.Authorization = agentApp.Authorization;
        row.BindingState = agentApp.BindingState;
        row.BotUserId = agentApp.BotUserId;
        row.VerifiedScopesJson = agentApp.VerifiedScopesJson;
        row.InstallUrl = agentApp.InstallUrl;
        row.RuntimeCredentialValidationState = agentApp.RuntimeCredentialValidationState;
        row.ClientSecretRef = agentApp.ClientSecretRef;
        row.SigningSecretRef = agentApp.SigningSecretRef;
        row.AppLevelTokenRef = agentApp.AppLevelTokenRef;
        row.BotTokenRef = agentApp.BotTokenRef;
        row.DeletedAt = agentApp.DeletedAt;
    }

}
