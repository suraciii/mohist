using System.Text;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mohist.Server.Slack.Services;

public sealed class SlackManagerApplicationService : IScopedService
{
    private const string ProductCapabilityVersion = "p0-agent-app";
    private const int ManifestVersion = 2;
    private static readonly string[] BotScopes = ["app_mentions:read", "chat:write"];
    private static readonly string[] BotEvents = ["app_mention"];

    private readonly AgentQuerier _agents;
    private readonly AgentConnectionStore _connections;
    private readonly SlackWorkspaceEnrollmentStore _enrollments;
    private readonly ManagedSlackChildAppStore _childApps;
    private readonly SlackManifestGenerator _manifests;
    private readonly ManagedSlackChildAppApplicationService _childOperations;
    private readonly SlackOAuthStateService _oauthStates;
    private readonly SlackOAuthAuthorizationService _oauthAuthorization;
    private readonly ManagerClaimService _claims;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ISecretStore _secrets;

    public SlackManagerApplicationService(
        AgentQuerier agents,
        AgentConnectionStore connections,
        SlackWorkspaceEnrollmentStore enrollments,
        ManagedSlackChildAppStore childApps,
        SlackManifestGenerator manifests,
        ManagedSlackChildAppApplicationService childOperations,
        SlackOAuthStateService oauthStates,
        SlackOAuthAuthorizationService oauthAuthorization,
        ManagerClaimService claims,
        IDbContextFactory<MohistDbContext> dbFactory,
        ISecretStore secrets)
    {
        _agents = agents;
        _connections = connections;
        _enrollments = enrollments;
        _childApps = childApps;
        _manifests = manifests;
        _childOperations = childOperations;
        _oauthStates = oauthStates;
        _oauthAuthorization = oauthAuthorization;
        _claims = claims;
        _dbFactory = dbFactory;
        _secrets = secrets;
    }

    public async Task<SlackManagerSetupResult> SetupAsync(
        SlackManagerSetupRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceTeamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ManagerAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ManagerBotUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ManagerCredentialRef);
        SlackStateTransitions.RequireManagerTransportKind(request.TransportKind);
        SlackStateTransitions.RequireManagerReadiness(request.Readiness);

        var enrollment = await _enrollments.GetByTeamAsync(request.WorkspaceTeamId.Trim(), ct);
        if (enrollment is not null && enrollment.Lifecycle == SlackEnrollmentLifecycle.Removed)
            throw new SlackManagerConflictException(
                "The workspace enrollment was removed and cannot be reused.",
                "enrollment_removed");

        if (enrollment is null)
        {
            enrollment = new SlackWorkspaceEnrollment
            {
                Id = $"enrollment_{Guid.NewGuid():N}",
                WorkspaceTeamId = request.WorkspaceTeamId.Trim(),
                ManagerActorId = $"manager_actor_{Guid.NewGuid():N}",
                ManagerCapability = SlackManagerCapability.Available,
                PlanCode = "unknown",
                ManagedAppLimit = 0,
            };
            try
            {
                enrollment = await _enrollments.CreateAsync(enrollment, ct);
            }
            catch (DbUpdateException)
            {
                enrollment = await _enrollments.GetByTeamAsync(request.WorkspaceTeamId.Trim(), ct)
                    ?? throw new InvalidOperationException(
                        "The workspace enrollment could not be recovered after a concurrent setup.");
            }
        }
        else if (enrollment.Lifecycle == SlackEnrollmentLifecycle.Disabled)
        {
            enrollment = await _enrollments.TransitionLifecycleAsync(
                enrollment.Id, SlackEnrollmentLifecycle.Active, ct)
                ?? throw new InvalidOperationException("The workspace enrollment disappeared during setup.");
        }

        if (string.IsNullOrWhiteSpace(enrollment.ManagerActorId))
            enrollment = await _enrollments.EnsureManagerActorAsync(
                enrollment.Id,
                $"manager_actor_{Guid.NewGuid():N}",
                ct) ?? throw new InvalidOperationException("The workspace enrollment disappeared during setup.");

        enrollment = await _enrollments.ConfigureManagerAppAsync(
            enrollment.Id,
            request.ManagerAppId.Trim(),
            request.ManagerBotUserId.Trim(),
            request.ManagerCredentialRef.Trim(),
            request.TransportKind,
            request.Readiness,
            ct) ?? throw new InvalidOperationException("The workspace enrollment disappeared during setup.");

        var claim = string.IsNullOrWhiteSpace(enrollment.ClaimedSlackUserId)
            ? await _claims.IssueAsync(enrollment.Id, ct)
            : SlackManagerClaimIssued.None;
        var credentialProvisioned = await HasManagerCredentialAsync(enrollment, ct);
        return new(
            ProjectEnrollment(enrollment, credentialProvisioned),
            claim.Code,
            claim.ExpiresAt,
            NextAction(enrollment, credentialProvisioned));
    }

    public async Task<SlackManagerCredentialProvisionResult> ProvisionManagerCredentialAsync(
        string workspaceTeamId,
        string managerBotToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceTeamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(managerBotToken);

        var enrollment = await _enrollments.GetActiveByTeamAsync(workspaceTeamId.Trim(), ct)
            ?? throw new SlackManagerConflictException(
                "Run Manager setup for this workspace before provisioning its credential.",
                "enrollment_required");
        if (string.IsNullOrWhiteSpace(enrollment.ManagerCredentialRef))
            throw new SlackManagerConflictException(
                "Manager setup has not configured a credential reference.",
                "manager_credential_reference_required");

        await _secrets.StoreAsync(
            ManagerCredentialAddress(enrollment.ManagerCredentialRef),
            Encoding.UTF8.GetBytes(managerBotToken.Trim()),
            ct);

        return new(enrollment.WorkspaceTeamId, true);
    }

    public async Task<SlackManagerStatusProjection?> GetStatusAsync(
        string workspaceTeamId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceTeamId);
        var normalizedTeamId = workspaceTeamId.Trim();
        var enrollment = await _enrollments.GetByTeamAsync(normalizedTeamId, ct);
        if (enrollment is null)
            return null;

        var children = await _childApps.ListByEnrollmentAsync(enrollment.Id, ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var connections = await db.AgentConnections.AsNoTracking()
            .Where(connection => connection.ProviderKind == ConnectionProviderKind.Slack
                && connection.WorkspaceTeamId == normalizedTeamId)
            .OrderBy(connection => connection.ProjectId)
            .ThenBy(connection => connection.Id)
            .Select(connection => new SlackManagerConnectionStatus(
                connection.ProjectId,
                connection.Id,
                connection.AgentId,
                connection.WorkspaceTeamId,
                connection.AppId,
                connection.BotUserId,
                connection.SetupProgress,
                connection.DesiredState,
                connection.ConnectionHealth,
                connection.HealthReason,
                connection.AgentReadiness,
                connection.DeletedAt))
            .ToListAsync(ct);

        var credentialProvisioned = await HasManagerCredentialAsync(enrollment, ct);
        return new(
            ProjectEnrollment(enrollment, credentialProvisioned),
            connections,
            children.Select(ProjectChild).ToList(),
            NextAction(enrollment, credentialProvisioned));
    }

    public async Task<IReadOnlyList<SlackManagerAgentOption>> ListAgentOptionsAsync(
        string projectId,
        string workspaceTeamId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var agents = await _agents.ListActiveDefinitionsAsync(projectId, ct);
        var connections = string.IsNullOrWhiteSpace(workspaceTeamId)
            ? Array.Empty<AgentConnection>()
            : await _connections.ListAsync(projectId, ct: ct);
        var options = new List<SlackManagerAgentOption>(agents.Count);
        foreach (var agent in agents)
        {
            var connection = connections.FirstOrDefault(item =>
                item.AgentId == agent.Id && item.WorkspaceTeamId == workspaceTeamId);
            var child = connection is null
                ? null
                : await _childApps.GetByConnectionAsync(connection.Id, ct);
            options.Add(new(
                agent.Id,
                agent.Name,
                agent.Description,
                SlackBotIdentityDeriver.Derive(agent),
                connection is null ? null : ProjectConnection(connection),
                child is null ? null : ProjectChild(child)));
        }
        return options;
    }

    public async Task<SlackManagerCreateResult> CreateAsync(
        SlackManagerCreateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceTeamId);
        ValidateAccessPolicy(request.AccessPolicy);
        SlackStateTransitions.RequireTransportKind(request.TransportKind);

        var agent = await _agents.GetByIdAsync(request.ProjectId, request.AgentId, ct);
        if (agent is null)
            throw new SlackManagerValidationException("The Agent was not found.", "agent_not_found");
        if (agent.Status != AgentStatus.Active)
            throw new SlackManagerValidationException("Only active Agents can receive a managed App.", "agent_archived");

        var enrollment = await _enrollments.GetActiveByTeamAsync(request.WorkspaceTeamId, ct)
            ?? throw new SlackManagerConflictException(
                "Run Manager setup for this workspace before creating a managed Connection.",
                "enrollment_required");
        var existing = (await _connections.ListAsync(request.ProjectId, ct: ct))
            .FirstOrDefault(connection =>
                connection.AgentId == request.AgentId
                && connection.WorkspaceTeamId == request.WorkspaceTeamId);
        if (existing is not null)
        {
            var existingChild = await _childApps.GetByConnectionAsync(existing.Id, ct);
            if (existingChild is null)
                existingChild = await CreateChildAsync(existing, enrollment, agent, request, ct);
            return new(false, ProjectConnection(existing), ProjectChild(existingChild),
                SlackBotIdentityDeriver.Derive(agent));
        }

        if (await _childApps.HasUndeletedForAgentAndWorkspaceAsync(
                request.ProjectId, request.AgentId, request.WorkspaceTeamId, ct))
            throw new SlackManagerConflictException(
                "An undeleted managed App still owns this Agent/workspace binding. Permanently delete that App before creating another one.",
                "managed_app_exists");

        var preview = SlackBotIdentityDeriver.Derive(agent);
        var connection = new AgentConnection
        {
            Id = $"connection_{Guid.NewGuid():N}",
            ProjectId = request.ProjectId,
            AgentId = request.AgentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = request.WorkspaceTeamId.Trim(),
            BotName = request.BotName?.Trim() ?? preview.BotName,
            AvatarHash = request.AvatarHash,
            SetupProgress = SetupProgressKind.CreateAppCredentials,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Unhealthy,
            HealthReason = "managed_app_not_ready",
            AgentReadiness = AgentReadinessDeriver.Derive(agent.AgentConfig),
            OwnerSlackUserId = request.OwnerSlackUserId,
            AccessPolicy = request.AccessPolicy,
        };

        try
        {
            connection = await _connections.CreateStagedAsync(connection, ct);
        }
        catch (AgentConnectionDuplicateException)
        {
            var raced = (await _connections.ListAsync(request.ProjectId, ct: ct))
                .FirstOrDefault(item => item.AgentId == request.AgentId
                    && item.WorkspaceTeamId == request.WorkspaceTeamId);
            if (raced is null) throw;
            var racedChild = await _childApps.GetByConnectionAsync(raced.Id, ct)
                ?? await CreateChildAsync(raced, enrollment, agent, request, ct);
            return new(false, ProjectConnection(raced), ProjectChild(racedChild), preview);
        }

        var child = await CreateChildAsync(connection, enrollment, agent, request, ct);
        return new(true, ProjectConnection(connection), ProjectChild(child), preview);
    }

    public async Task<SlackManagerAppProjection?> GetAsync(
        string projectId,
        string connectionId,
        CancellationToken ct = default)
    {
        var connection = await _connections.GetAsync(projectId, connectionId, ct);
        if (connection is null) return null;
        var child = await _childApps.GetByConnectionAsync(connectionId, ct);
        return child is null ? null : ProjectChild(child);
    }

    public async Task<ManagedSlackChildAppOperationResult> CreateChildAppAsync(
        string projectId,
        string connectionId,
        CancellationToken ct = default) =>
        await RunChildOperationAsync(projectId, connectionId, _childOperations.CreateAsync, ct);

    public async Task<ManagedSlackChildAppOperationResult> ReconcileCreateAsync(
        string projectId,
        string connectionId,
        CancellationToken ct = default) =>
        await RunChildOperationAsync(projectId, connectionId, _childOperations.ReconcileCreateAsync, ct);

    public async Task<ManagedSlackChildAppOperationResult> PermanentDeleteAsync(
        string projectId,
        string connectionId,
        string confirmation,
        CancellationToken ct = default)
    {
        var child = await FindChildAsync(projectId, connectionId, ct);
        if (child is null) return ManagedSlackChildAppOperationResult.NotFound;
        return await _childOperations.DeleteAsync(child.Id, confirmation, ct);
    }

    public async Task<ManagedSlackChildAppOperationResult> ReconcileDeleteAsync(
        string projectId,
        string connectionId,
        CancellationToken ct = default) =>
        await RunChildOperationAsync(projectId, connectionId, _childOperations.ReconcileDeleteAsync, ct);

    public async Task<SlackOAuthStateIssued> BeginAuthorizationAsync(
        string projectId,
        string connectionId,
        CancellationToken ct = default)
    {
        var child = await RequireChildAsync(projectId, connectionId, ct);
        if (child.AppLifecycle != SlackAppLifecycle.Created || string.IsNullOrWhiteSpace(child.AppId))
            throw new SlackManagerConflictException("The Child App must be created before authorization.", "child_app_not_created");
        await _oauthAuthorization.RecordProgressAsync(child.Id, SlackAuthorizationState.AwaitingUser, ct);
        return await _oauthStates.IssueAsync(child.Id, child.WorkspaceTeamId, child.AppId, ct: ct);
    }

    public Task<SlackOAuthAuthorizationResult> RecordAuthorizationProgressAsync(
        string projectId,
        string connectionId,
        string authorization,
        CancellationToken ct = default) =>
        RunOAuthOperationAsync(projectId, connectionId,
            child => _oauthAuthorization.RecordProgressAsync(child.Id, authorization, ct), ct);

    public Task<SlackOAuthAuthorizationResult> AuthorizeAsync(
        string projectId,
        string connectionId,
        string state,
        string botUserId,
        string botToken,
        CancellationToken ct = default) =>
        RunOAuthOperationAsync(projectId, connectionId, async child =>
            await _oauthAuthorization.AuthorizeAsync(
                state,
                child.Id,
                child.WorkspaceTeamId,
                child.AppId,
                botUserId,
                botToken,
                ct), ct);

    private async Task<ManagedSlackChildApp> CreateChildAsync(
        AgentConnection connection,
        SlackWorkspaceEnrollment enrollment,
        AgentInfo agent,
        SlackManagerCreateRequest request,
        CancellationToken ct)
    {
        var preview = SlackBotIdentityDeriver.Derive(agent);
        var manifest = _manifests.Generate(new SlackManifestInput(
            connection.BotName,
            preview.AppDescription,
            BotScopes,
            request.TransportKind == SlackTransportKind.Https
                ? SlackManifestTransport.Https
                : SlackManifestTransport.Socket,
            request.PublicIngressBaseUrl,
            ProductCapabilityVersion,
            new SlackManifestIdentitySnapshot(connection.Id, connection.AgentId, connection.WorkspaceTeamId),
            ManifestVersion,
            BotEvents));
        return await _childApps.CreateAsync(new ManagedSlackChildApp
        {
            Id = $"child_app_{Guid.NewGuid():N}",
            EnrollmentId = enrollment.Id,
            WorkspaceTeamId = connection.WorkspaceTeamId,
            AgentConnectionId = connection.Id,
            PublicIngressBaseUrl = request.PublicIngressBaseUrl,
            TransportKind = request.TransportKind,
            DesiredManifestVersion = manifest.Version,
            DesiredManifestHash = manifest.Hash,
        }, ct);
    }

    private async Task<ManagedSlackChildApp> RequireChildAsync(
        string projectId,
        string connectionId,
        CancellationToken ct)
    {
        var connection = await _connections.GetAsync(projectId, connectionId, ct);
        if (connection is null)
            throw new SlackManagerNotFoundException("The Slack Connection was not found.");
        return await _childApps.GetByConnectionAsync(connectionId, ct)
            ?? throw new SlackManagerNotFoundException("The managed Child App was not found.");
    }

    private async Task<ManagedSlackChildAppOperationResult> RunChildOperationAsync(
        string projectId,
        string connectionId,
        Func<string, CancellationToken, Task<ManagedSlackChildAppOperationResult>> operation,
        CancellationToken ct)
    {
        var child = await FindChildAsync(projectId, connectionId, ct);
        return child is null
            ? ManagedSlackChildAppOperationResult.NotFound
            : await operation(child.Id, ct);
    }

    private async Task<ManagedSlackChildApp?> FindChildAsync(
        string projectId,
        string connectionId,
        CancellationToken ct)
    {
        var connection = await _connections.GetAsync(projectId, connectionId, ct);
        if (connection is null) return null;
        var child = await _childApps.GetByConnectionAsync(connectionId, ct);
        return child;
    }

    private async Task<SlackOAuthAuthorizationResult> RunOAuthOperationAsync(
        string projectId,
        string connectionId,
        Func<ManagedSlackChildApp, Task<SlackOAuthAuthorizationResult>> operation,
        CancellationToken ct)
    {
        var child = await RequireChildAsync(projectId, connectionId, ct);
        return await operation(child);
    }

    private static SlackManagerConnectionProjection ProjectConnection(AgentConnection connection) => new(
        connection.Id,
        connection.AgentId,
        connection.WorkspaceTeamId,
        connection.AppId,
        connection.BotUserId,
        connection.BotName,
        connection.AvatarHash,
        connection.SetupProgress,
        connection.DesiredState,
        connection.ConnectionHealth,
        connection.HealthReason,
        connection.AgentReadiness,
        connection.OwnerSlackUserId,
        connection.AccessPolicy,
        connection.DeletedAt);

    private static SlackManagerEnrollmentProjection ProjectEnrollment(
        SlackWorkspaceEnrollment enrollment,
        bool credentialProvisioned) => new(
        enrollment.Id,
        enrollment.WorkspaceTeamId,
        enrollment.Lifecycle,
        enrollment.ManagerCapability,
        enrollment.ManagerAppId,
        enrollment.ManagerBotUserId,
        enrollment.ManagerTransportKind,
        enrollment.ManagerReadiness,
        !string.IsNullOrWhiteSpace(enrollment.ManagerCredentialRef),
        credentialProvisioned,
        enrollment.ClaimedSlackUserId,
        enrollment.UpdatedAt);

    private static string NextAction(
        SlackWorkspaceEnrollment enrollment,
        bool credentialProvisioned)
    {
        if (enrollment.Lifecycle == SlackEnrollmentLifecycle.Removed)
            return "setup";
        if (string.IsNullOrWhiteSpace(enrollment.ManagerAppId)
            || string.IsNullOrWhiteSpace(enrollment.ManagerBotUserId)
            || string.IsNullOrWhiteSpace(enrollment.ManagerCredentialRef)
            || enrollment.ManagerReadiness != SlackManagerReadiness.Ready)
            return "configure_manager_app";
        if (!credentialProvisioned)
            return "configure_manager_credentials";
        return string.IsNullOrWhiteSpace(enrollment.ClaimedSlackUserId)
            ? "claim_manager"
            : "ready";
    }

    private async Task<bool> HasManagerCredentialAsync(
        SlackWorkspaceEnrollment enrollment,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(enrollment.ManagerCredentialRef))
            return false;

        var secret = await _secrets.LoadAsync(
            ManagerCredentialAddress(enrollment.ManagerCredentialRef),
            ct);
        return secret is { Length: > 0 };
    }

    private static SecretStoreAddress ManagerCredentialAddress(string credentialRef) => new(
        SlackDeliveryOwnerIds.ManagerProjectId,
        credentialRef,
        SecretKind.BotToken);

    private static SlackManagerAppProjection ProjectChild(ManagedSlackChildApp child)
    {
        var status = ManagedSlackChildAppStatusDeriver.Derive(child);
        return new(
            child.Id,
            child.EnrollmentId,
            child.AgentConnectionId,
            child.WorkspaceTeamId,
            child.AppId,
            child.BotUserId,
            child.AppLifecycle,
            child.Authorization,
            status.ManifestState,
            child.TransportKind,
            status.TransportReadiness,
            status.NextAction,
            child.BindingState,
            child.UnknownOutcome,
            child.ErrorClass,
            child.DeletedAt);
    }

    private static void ValidateAccessPolicy(string value)
    {
        if (value is not (AccessPolicyKind.OwnerOnly or AccessPolicyKind.Allowlist or AccessPolicyKind.Anyone))
            throw new SlackManagerValidationException("Unknown access policy.", "invalid_access_policy");
    }
}

public sealed record SlackManagerCreateRequest(
    string ProjectId,
    string AgentId,
    string WorkspaceTeamId,
    string AccessPolicy = AccessPolicyKind.OwnerOnly,
    string? OwnerSlackUserId = null,
    string? BotName = null,
    string? AvatarHash = null,
    string TransportKind = SlackTransportKind.Socket,
    string? PublicIngressBaseUrl = null);

public sealed record SlackManagerSetupRequest(
    string WorkspaceTeamId,
    string ManagerAppId,
    string ManagerBotUserId,
    string ManagerCredentialRef,
    string TransportKind = SlackManagerTransportKind.Socket,
    string Readiness = SlackManagerReadiness.Ready);

public sealed record SlackManagerCredentialProvisionResult(
    string WorkspaceTeamId,
    bool CredentialProvisioned);

public sealed record SlackManagerClaimIssued(
    string? Code,
    DateTimeOffset? ExpiresAt)
{
    public static SlackManagerClaimIssued None { get; } = new(null, null);
}

public sealed record SlackManagerSetupResult(
    SlackManagerEnrollmentProjection Enrollment,
    string? ClaimCode,
    DateTimeOffset? ClaimExpiresAt,
    string NextAction);

public sealed record SlackManagerEnrollmentProjection(
    string Id,
    string WorkspaceTeamId,
    string Lifecycle,
    string ManagerCapability,
    string ManagerAppId,
    string ManagerBotUserId,
    string ManagerTransportKind,
    string ManagerReadiness,
    bool ManagerCredentialConfigured,
    bool ManagerCredentialProvisioned,
    string? ClaimedSlackUserId,
    DateTimeOffset UpdatedAt);

public sealed record SlackManagerStatusProjection(
    SlackManagerEnrollmentProjection Enrollment,
    IReadOnlyList<SlackManagerConnectionStatus> Connections,
    IReadOnlyList<SlackManagerAppProjection> ManagedApps,
    string NextAction);

public sealed record SlackManagerConnectionStatus(
    string ProjectId,
    string ConnectionId,
    string AgentId,
    string WorkspaceTeamId,
    string AppId,
    string BotUserId,
    string SetupProgress,
    string DesiredState,
    string ConnectionHealth,
    string? HealthReason,
    string AgentReadiness,
    DateTimeOffset? DeletedAt);

public sealed record SlackManagerAgentOption(
    string AgentId,
    string AgentName,
    string AgentDescription,
    SlackBotIdentityPreview Preview,
    SlackManagerConnectionProjection? Connection,
    SlackManagerAppProjection? ManagedApp);

public sealed record SlackManagerCreateResult(
    bool Created,
    SlackManagerConnectionProjection Connection,
    SlackManagerAppProjection ManagedApp,
    SlackBotIdentityPreview Preview);

public sealed record SlackManagerConnectionProjection(
    string Id,
    string AgentId,
    string WorkspaceTeamId,
    string AppId,
    string BotUserId,
    string BotName,
    string? AvatarHash,
    string SetupProgress,
    string DesiredState,
    string ConnectionHealth,
    string? HealthReason,
    string AgentReadiness,
    string? OwnerSlackUserId,
    string AccessPolicy,
    DateTimeOffset? DeletedAt);

public sealed record SlackManagerAppProjection(
    string Id,
    string EnrollmentId,
    string AgentConnectionId,
    string WorkspaceTeamId,
    string AppId,
    string BotUserId,
    string AppLifecycle,
    string Authorization,
    string ManifestState,
    string TransportKind,
    string TransportReadiness,
    string NextAction,
    string BindingState,
    string? UnknownOutcome,
    string? ErrorClass,
    DateTimeOffset? DeletedAt);

public sealed class SlackManagerValidationException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class SlackManagerConflictException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class SlackManagerNotFoundException(string message) : Exception(message);
