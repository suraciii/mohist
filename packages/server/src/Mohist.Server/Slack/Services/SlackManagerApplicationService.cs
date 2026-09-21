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

public sealed partial class SlackManagerApplicationService : IScopedService
{
    private const string ProductCapabilityVersion = "p0-agent-app";
    private const int ManifestVersion = 2;
    private static readonly string[] BotScopes = ["app_mentions:read", "chat:write"];
    private static readonly string[] BotEvents = ["app_mention"];

    private readonly AgentQuerier _agents;
    private readonly AgentConnectionStore _connections;
    private readonly SlackWorkspaceEnrollmentStore _enrollments;
    private readonly ManagedSlackAgentAppStore _agentApps;
    private readonly SlackManifestGenerator _manifests;
    private readonly ManagedSlackAgentAppApplicationService _childOperations;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ISecretStore _secrets;
    private readonly SlackConnectionAccessManager _accessPolicies;
    private readonly SlackOwnerClaimService _ownerClaims;
    private readonly SlackOutboxStore _outbox;
    private readonly IGrainFactory _grains;
    private readonly ManagerAgentDefaultProfileResolver _defaults;

    public SlackManagerApplicationService(
        AgentQuerier agents,
        AgentConnectionStore connections,
        SlackWorkspaceEnrollmentStore enrollments,
        ManagedSlackAgentAppStore agentApps,
        SlackManifestGenerator manifests,
        ManagedSlackAgentAppApplicationService childOperations,
        IDbContextFactory<MohistDbContext> dbFactory,
        ISecretStore secrets,
        SlackConnectionAccessManager accessPolicies,
        SlackOwnerClaimService ownerClaims,
        SlackOutboxStore outbox,
        IGrainFactory grains,
        ManagerAgentDefaultProfileResolver defaults)
    {
        _agents = agents;
        _connections = connections;
        _enrollments = enrollments;
        _agentApps = agentApps;
        _manifests = manifests;
        _childOperations = childOperations;
        _dbFactory = dbFactory;
        _secrets = secrets;
        _accessPolicies = accessPolicies;
        _ownerClaims = ownerClaims;
        _outbox = outbox;
        _grains = grains;
        _defaults = defaults;
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

        var agentApps = await _agentApps.ListByEnrollmentAsync(enrollment.Id, ct);
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
                connection.OwnerSlackUserId,
                connection.DeletedAt))
            .ToListAsync(ct);

        var credentialProvisioned = await HasManagerCredentialAsync(enrollment, ct);
        return new(
            ProjectEnrollment(enrollment, credentialProvisioned),
            connections,
            agentApps.Select(app =>
            {
                var bound = FindConnection(connections, app);
                return ProjectChild(app, bound?.OwnerSlackUserId, bound?.AgentReadiness ?? AgentReadinessKind.Ready);
            }).ToList(),
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
            var agentApp = connection is null
                ? null
                : await _agentApps.GetByConnectionAsync(connection.Id, ct);
            options.Add(new(
                agent.Id,
                agent.Name,
                agent.Description,
                SlackBotIdentityDeriver.Derive(agent),
                connection is null ? null : ProjectConnection(connection),
                agentApp is null
                    ? null
                    : ProjectChild(agentApp, connection!.OwnerSlackUserId, connection.AgentReadiness)));
        }
        return options;
    }

    public async Task<SlackManagerAppProjection?> GetAsync(
        string projectId,
        string connectionId,
        CancellationToken ct = default)
    {
        var connection = await _connections.GetAsync(projectId, connectionId, ct);
        if (connection is null) return null;
        var agentApp = await _agentApps.GetByConnectionAsync(connectionId, ct);
        return agentApp is null
            ? null
            : ProjectChild(agentApp, connection.OwnerSlackUserId, connection.AgentReadiness);
    }

    public async Task<ManagedSlackAgentAppOperationResult> PermanentDeleteAsync(
        string projectId,
        string connectionId,
        string confirmation,
        CancellationToken ct = default)
    {
        var agentApp = await FindChildAsync(projectId, connectionId, ct);
        if (agentApp is null) return ManagedSlackAgentAppOperationResult.NotFound;
        return await _childOperations.DeleteAsync(agentApp.Id, confirmation, ct);
    }

    public async Task<ManagedSlackAgentAppOperationResult> ReconcileDeleteAsync(
        string projectId,
        string connectionId,
        CancellationToken ct = default) =>
        await RunChildOperationAsync(projectId, connectionId, _childOperations.ReconcileDeleteAsync, ct);

    private async Task<ManagedSlackAgentAppOperationResult> RunChildOperationAsync(
        string projectId,
        string connectionId,
        Func<string, CancellationToken, Task<ManagedSlackAgentAppOperationResult>> operation,
        CancellationToken ct)
    {
        var agentApp = await FindChildAsync(projectId, connectionId, ct);
        return agentApp is null
            ? ManagedSlackAgentAppOperationResult.NotFound
            : await operation(agentApp.Id, ct);
    }

    private async Task<ManagedSlackAgentApp?> FindChildAsync(
        string projectId,
        string connectionId,
        CancellationToken ct)
    {
        var connection = await _connections.GetAsync(projectId, connectionId, ct);
        if (connection is null) return null;
        var agentApp = await _agentApps.GetByConnectionAsync(connectionId, ct);
        return agentApp;
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

    /// <summary>
    /// The Connection an Agent App is bound to, when the projection carries it.
    /// </summary>
    private static SlackManagerConnectionStatus? FindConnection(
        IReadOnlyList<SlackManagerConnectionStatus> connections,
        ManagedSlackAgentApp agentApp) =>
        connections.FirstOrDefault(connection => string.Equals(
            connection.ConnectionId,
            agentApp.AgentConnectionId,
            StringComparison.Ordinal));

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
            ManagerCredentialAddress(enrollment.Id),
            ct);
        return secret is { Length: > 0 };
    }

    private static SecretStoreAddress ManagerCredentialAddress(string enrollmentId) =>
        SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.BotToken);

    /// <summary>
    /// A technically ready App is not the end of the Connection's setup: while
    /// the Owner claim is outstanding the projected action is the claim, and a
    /// complete Connection whose Agent cannot execute points at the existing
    /// Agent repair surface.
    /// </summary>
    private static SlackManagerAppProjection ProjectChild(
        ManagedSlackAgentApp agentApp,
        string? ownerSlackUserId = null,
        string agentReadiness = AgentReadinessKind.Ready)
    {
        var status = ManagedSlackAgentAppStatusDeriver.Derive(agentApp);
        var nextAction = status.NextAction == SlackAgentAppNextAction.Ready
            ? ownerSlackUserId is null
                ? SlackAgentAppNextAction.ClaimOwner
                : string.Equals(agentReadiness, AgentReadinessKind.Ready, StringComparison.Ordinal)
                    ? SlackAgentAppNextAction.Ready
                    : SlackAgentAppNextAction.RepairAgent
            : status.NextAction;
        return new(
            agentApp.Id,
            agentApp.EnrollmentId,
            agentApp.AgentConnectionId,
            agentApp.WorkspaceTeamId,
            agentApp.AppId,
            agentApp.BotUserId,
            agentApp.AppLifecycle,
            agentApp.Authorization,
            status.ManifestState,
            SlackManagerTransportKind.Socket,
            status.TransportReadiness,
            nextAction,
            agentApp.BindingState,
            string.IsNullOrWhiteSpace(agentApp.InstallUrl) ? null : agentApp.InstallUrl,
            agentApp.UnknownOutcome,
            agentApp.ErrorClass,
            agentApp.DeletedAt);
    }

    private static void ValidateAccessPolicy(string value)
    {
        if (value is not (AccessPolicyKind.OwnerOnly or AccessPolicyKind.Allowlist or AccessPolicyKind.Anyone))
            throw new SlackManagerValidationException("Unknown access policy.", "invalid_access_policy");
    }
}

public sealed record SlackManagerClaimIssued(
    string? Code,
    DateTimeOffset? ExpiresAt)
{
    public static SlackManagerClaimIssued None { get; } = new(null, null);
}

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
    string? OwnerSlackUserId,
    DateTimeOffset? DeletedAt);

public sealed record SlackManagerAgentOption(
    string AgentId,
    string AgentName,
    string AgentDescription,
    SlackBotIdentityPreview Preview,
    SlackManagerConnectionProjection? Connection,
    SlackManagerAppProjection? ManagedApp);

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
    string? InstallUrl,
    string? UnknownOutcome,
    string? ErrorClass,
    DateTimeOffset? DeletedAt);

public sealed class SlackManagerValidationException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class SlackManagerConflictException(string message, string code, object? details = null) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>
    /// Machine-readable context the caller needs to act, such as the Workspace
    /// choices an ambiguous selector must resolve to.
    /// </summary>
    public object? Details { get; } = details;
}
