using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Idempotent <c>install-agent</c> orchestration. The idempotency key is
/// (enrollment_id, AgentId): every entry resolves the same staged
/// Connection and ManagedSlackAgentApp and advances at most one pending
/// step (manifest create, credential staging, Socket hello validation,
/// Connection bind) before returning the full progress and the unique
/// next action. Reruns repair drift on the same records and never create
/// a second Connection or Agent App.
/// </summary>
public sealed class SlackInstallAgentService : IScopedService
{
    private const string ProductCapabilityVersion = "p0-agent-app";
    private const int ManifestVersion = 2;

    private static readonly IReadOnlyCollection<string> RequiredAgentBotScopes =
        SlackManifestDefinition.For(SlackManifestKind.AgentApp).BotScopes;

    private readonly AgentQuerier _agents;
    private readonly AgentConnectionStore _connections;
    private readonly SlackWorkspaceEnrollmentStore _enrollments;
    private readonly ManagedSlackAgentAppStore _agentApps;
    private readonly SlackManifestGenerator _manifests;
    private readonly ManagedSlackAgentAppApplicationService _agentAppOperations;
    private readonly SlackAgentAppBindingService _binding;
    private readonly ISlackAppManagementPort _appManagement;
    private readonly ISlackBotIdentityVerificationPort _botIdentity;
    private readonly ISecretStore _secrets;

    public SlackInstallAgentService(
        AgentQuerier agents,
        AgentConnectionStore connections,
        SlackWorkspaceEnrollmentStore enrollments,
        ManagedSlackAgentAppStore agentApps,
        SlackManifestGenerator manifests,
        ManagedSlackAgentAppApplicationService agentAppOperations,
        SlackAgentAppBindingService binding,
        ISlackAppManagementPort appManagement,
        ISlackBotIdentityVerificationPort botIdentity,
        ISecretStore secrets)
    {
        _agents = agents;
        _connections = connections;
        _enrollments = enrollments;
        _agentApps = agentApps;
        _manifests = manifests;
        _agentAppOperations = agentAppOperations;
        _binding = binding;
        _appManagement = appManagement;
        _botIdentity = botIdentity;
        _secrets = secrets;
    }

    public async Task<SlackInstallAgentProgress> InstallAsync(
        string projectId,
        string agentId,
        string enrollmentId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentId);

        var enrollment = await _enrollments.GetAsync(enrollmentId, ct)
            ?? throw new SlackManagerConflictException("Run Slack setup for this workspace first.", "enrollment_required");
        if (enrollment.Lifecycle != SlackEnrollmentLifecycle.Active || enrollment.DeletedAt is not null)
            throw new SlackManagerConflictException("The workspace enrollment is not active.", "enrollment_not_active");

        var agent = await _agents.GetByIdAsync(projectId, agentId, ct)
            ?? throw new SlackManagerConflictException("The Agent was not found.", "agent_not_found");
        if (agent.Status != AgentStatus.Active)
            throw new SlackManagerConflictException("Only active Agents can be installed to Slack.", "agent_archived");

        var connection = (await _connections.ListAsync(projectId, ct: ct))
            .FirstOrDefault(item => item.AgentId == agentId
                && item.WorkspaceTeamId == enrollment.WorkspaceTeamId);
        if (connection is null)
        {
            if (await _agentApps.HasUndeletedForAgentAndWorkspaceAsync(
                    projectId, agentId, enrollment.WorkspaceTeamId, ct))
                throw new SlackManagerConflictException(
                    "An undeleted Agent App still owns this Agent/workspace binding. Permanently delete that App before reinstalling.",
                    "managed_app_exists");
            connection = await CreateStagedConnectionAsync(enrollment, agent, projectId, ct);
        }

        var agentApp = await _agentApps.GetByConnectionAsync(connection.Id, ct);
        if (agentApp is null)
            agentApp = await CreateAgentAppAsync(connection, agent, enrollment, ct);

        agentApp = await EnsureDesiredManifestAsync(agentApp, connection, ct);
        return await AdvanceAsync(connection, agentApp, ct);
    }

    public async Task<SlackInstallAgentCredentialResult> ProvisionCredentialsAsync(
        string agentAppId,
        string botToken,
        string appLevelToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(appLevelToken);

        var agentApp = await _agentApps.GetAsync(agentAppId, ct)
            ?? throw new SlackManagerConflictException("The Agent App was not found.", "agent_app_not_found");
        if (agentApp.AppLifecycle != SlackAppLifecycle.Created || string.IsNullOrWhiteSpace(agentApp.AppId))
            throw new SlackManagerConflictException("The Agent App must be created before provisioning credentials.", "agent_app_not_created");

        // A verified App re-supplying the exact verified credentials is a
        // no-op; anything else re-validates and rotates through the same
        // candidate -> Socket hello path as the first provision.
        if (agentApp.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.Verified
            && await IsUnchangedRuntimeCredentialsAsync(agentApp.Id, botToken, appLevelToken, ct))
            return new SlackInstallAgentCredentialResult(true, agentApp.RuntimeCredentialValidationState);

        var verification = await _botIdentity.VerifyAsync(new SlackBotIdentityVerificationRequest(botToken), ct);
        if (!verification.Verified)
            return new SlackInstallAgentCredentialResult(
                false, await RejectOrRestoreAsync(agentApp, ct), verification.ErrorClass ?? "bot_identity_verification_failed");

        var identityMismatch = !string.Equals(verification.WorkspaceTeamId, agentApp.WorkspaceTeamId, StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(verification.AppId)
                && !string.Equals(verification.AppId, agentApp.AppId, StringComparison.Ordinal));
        if (identityMismatch)
            return new SlackInstallAgentCredentialResult(
                false, await RejectOrRestoreAsync(agentApp, ct), "identity_mismatch");

        if (!HasRequiredAgentBotScopes(verification.GrantedScopes))
            return new SlackInstallAgentCredentialResult(
                false, await RejectOrRestoreAsync(agentApp, ct), "missing_required_scopes");

        // Rotate: park the verified pair in the previous slot before the new
        // candidate overwrites the runtime addresses, so the old credentials
        // survive until the new Socket hello verifies.
        if (agentApp.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.Verified)
            await PreserveRuntimeSecretsAsync(agentApp.Id, ct);

        await _secrets.StoreAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken),
            Encoding.UTF8.GetBytes(botToken),
            ct);
        await _secrets.StoreAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken),
            Encoding.UTF8.GetBytes(appLevelToken),
            ct);

        var scopes = verification.GrantedScopes is { Count: > 0 }
            ? JsonSerializer.Serialize(verification.GrantedScopes.OrderBy(scope => scope, StringComparer.Ordinal))
            : "[]";
        var staged = await _agentApps.StageRuntimeCredentialsAsync(
            agentAppId,
            botTokenRef: agentAppId,
            appLevelTokenRef: agentAppId,
            botUserId: verification.BotUserId ?? string.Empty,
            verifiedScopesJson: scopes,
            ct)
            ?? throw new InvalidOperationException("The Agent App disappeared while staging credentials.");
        return new SlackInstallAgentCredentialResult(true, staged.RuntimeCredentialValidationState);
    }

    public async Task<SlackInstallAgentValidationResult> ApplySocketValidationAsync(
        string agentAppId,
        string helloAppId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(helloAppId);

        var agentApp = await _agentApps.GetAsync(agentAppId, ct)
            ?? throw new SlackManagerConflictException("The Agent App was not found.", "agent_app_not_found");
        if (agentApp.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.Verified)
        {
            var bound = await _binding.ReconcileAsync(agentAppId, ct);
            return new SlackInstallAgentValidationResult(SlackInstallAgentValidationOutcome.AlreadyVerified, bound.Status);
        }

        if (!string.Equals(helloAppId, agentApp.AppId, StringComparison.Ordinal))
        {
            await RejectOrRestoreAsync(agentApp, ct);
            return new SlackInstallAgentValidationResult(SlackInstallAgentValidationOutcome.Mismatch);
        }

        if (agentApp.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.Candidate)
        {
            await _agentApps.ApplyCredentialValidationAsync(
                agentAppId, SlackRuntimeCredentialValidationState.AwaitingSocket, ct);
        }
        // The confirmed hello makes the candidate the runtime pair; the
        // previous pair parked by a rotation is no longer needed.
        await DeletePreviousSecretsAsync(agentApp.Id, ct);
        await _agentApps.ApplyCredentialValidationAsync(agentAppId, SlackRuntimeCredentialValidationState.Verified, ct);
        var binding = await _binding.ReconcileAsync(agentAppId, ct);
        return new SlackInstallAgentValidationResult(SlackInstallAgentValidationOutcome.Verified, binding.Status);
    }

    private async Task<SlackInstallAgentProgress> AdvanceAsync(
        AgentConnection connection,
        ManagedSlackAgentApp agentApp,
        CancellationToken ct)
    {
        if (agentApp.AppLifecycle == SlackAppLifecycle.NotCreated)
        {
            var manifest = RegenerateManifest(connection, agentApp, ct);
            var validation = await _appManagement.ValidateManifestAsync(new SlackAppManifestRequest(
                new SlackAppManagementRequest(agentApp.EnrollmentId, agentApp.Id, agentApp.WorkspaceTeamId, agentApp.AppId),
                manifest), ct);
            if (validation.Outcome == SlackAppManagementOutcome.DefiniteFailure)
                return Progress(connection, agentApp, SlackAgentAppNextAction.CreateAgentApp,
                    validation.ErrorClass ?? "manifest_validation_failed");
            await _agentAppOperations.CreateAsync(agentApp.Id, ct);
            var current = await ReloadAsync(agentApp.Id, ct);
            return current.AppLifecycle switch
            {
                SlackAppLifecycle.Created => Progress(connection, current, SlackAgentAppNextAction.ProvideCredentials),
                SlackAppLifecycle.CreateUnknown => Progress(connection, current, SlackAgentAppNextAction.ReconcileCreate),
                _ => Progress(connection, current, SlackAgentAppNextAction.CreateAgentApp),
            };
        }

        return agentApp.AppLifecycle switch
        {
            SlackAppLifecycle.CreateUnknown => Progress(connection, agentApp, SlackAgentAppNextAction.ReconcileCreate),
            SlackAppLifecycle.Creating or SlackAppLifecycle.Deleting => Progress(connection, agentApp, SlackAgentAppNextAction.WaitForOperation),
            SlackAppLifecycle.Deleted => Progress(connection, agentApp, SlackAgentAppNextAction.Deleted),
            SlackAppLifecycle.Created when agentApp.RuntimeCredentialValidationState != SlackRuntimeCredentialValidationState.Verified
                => Progress(connection, agentApp, SlackAgentAppNextAction.ProvideCredentials),
            SlackAppLifecycle.Created when agentApp.BindingState != SlackAgentAppBindingState.Bound => await BindAsync(connection, agentApp, ct),
            SlackAppLifecycle.Created => Progress(connection, agentApp, SlackAgentAppNextAction.Ready),
            _ => Progress(connection, agentApp, SlackAgentAppNextAction.WaitForOperation),
        };
    }

    private async Task<SlackInstallAgentProgress> BindAsync(
        AgentConnection connection,
        ManagedSlackAgentApp agentApp,
        CancellationToken ct)
    {
        var binding = await _binding.ReconcileAsync(agentApp.Id, ct);
        var current = await ReloadAsync(agentApp.Id, ct);
        return Progress(connection, current, binding.Status == SlackAgentAppBindingStatus.Bound
            ? SlackAgentAppNextAction.Ready
            : SlackAgentAppNextAction.BindConnection);
    }

    private async Task<ManagedSlackAgentApp> EnsureDesiredManifestAsync(
        ManagedSlackAgentApp agentApp,
        AgentConnection connection,
        CancellationToken ct)
    {
        var manifest = RegenerateManifest(connection, agentApp, ct);
        if (agentApp.DesiredManifestVersion == manifest.Version
            && string.Equals(agentApp.DesiredManifestHash, manifest.Hash, StringComparison.Ordinal))
            return agentApp;
        return await _agentApps.UpdateDesiredManifestAsync(agentApp.Id, manifest.Version, manifest.Hash, ct)
            ?? throw new InvalidOperationException("The Agent App disappeared while updating its desired manifest.");
    }

    private SlackManifest RegenerateManifest(
        AgentConnection connection,
        ManagedSlackAgentApp agentApp,
        CancellationToken ct) =>
        _manifests.Generate(new SlackManifestInput(
            connection.BotName is { Length: > 0 } ? connection.BotName : "agent-app",
            string.Empty,
            ProductCapabilityVersion,
            new SlackManifestIdentitySnapshot(connection.Id, connection.AgentId, connection.WorkspaceTeamId),
            SlackManifestKind.AgentApp,
            ManifestVersion));

    private async Task<AgentConnection> CreateStagedConnectionAsync(
        SlackWorkspaceEnrollment enrollment,
        AgentInfo agent,
        string projectId,
        CancellationToken ct)
    {
        var connection = new AgentConnection
        {
            Id = $"connection_{Guid.NewGuid():N}",
            ProjectId = projectId,
            AgentId = agent.Id,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = enrollment.WorkspaceTeamId,
            SetupProgress = SetupProgressKind.CreateAppCredentials,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Unhealthy,
            HealthReason = "managed_app_not_ready",
            AgentReadiness = AgentReadinessDeriver.Derive(agent.AgentConfig),
            AccessPolicy = AccessPolicyKind.OwnerOnly,
        };
        try
        {
            return await _connections.CreateStagedAsync(connection, ct);
        }
        catch (AgentConnectionDuplicateException)
        {
            return (await _connections.ListAsync(projectId, ct: ct))
                .FirstOrDefault(item => item.AgentId == agent.Id
                    && item.WorkspaceTeamId == enrollment.WorkspaceTeamId)
                ?? throw new InvalidOperationException("The staged Connection was not found after a concurrent install.");
        }
    }

    private async Task<ManagedSlackAgentApp> CreateAgentAppAsync(
        AgentConnection connection,
        AgentInfo agent,
        SlackWorkspaceEnrollment enrollment,
        CancellationToken ct)
    {
        var manifest = _manifests.Generate(new SlackManifestInput(
            agent.Name,
            agent.Description ?? string.Empty,
            ProductCapabilityVersion,
            new SlackManifestIdentitySnapshot(connection.Id, connection.AgentId, connection.WorkspaceTeamId),
            SlackManifestKind.AgentApp,
            ManifestVersion));
        return await _agentApps.CreateAsync(new ManagedSlackAgentApp
        {
            Id = $"agent_app_{Guid.NewGuid():N}",
            EnrollmentId = enrollment.Id,
            WorkspaceTeamId = connection.WorkspaceTeamId,
            AgentConnectionId = connection.Id,
            DesiredManifestVersion = manifest.Version,
            DesiredManifestHash = manifest.Hash,
        }, ct);
    }

    private async Task<string> RejectOrRestoreAsync(ManagedSlackAgentApp agentApp, CancellationToken ct)
    {
        if (agentApp.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.Verified)
            return agentApp.RuntimeCredentialValidationState;
        // A rotation in flight parks the verified pair in the previous slot;
        // restore it so the App keeps its previously verified credentials.
        if (await HasPreviousSecretsAsync(agentApp.Id, ct))
        {
            await RestorePreviousSecretsAsync(agentApp.Id, ct);
            return (await _agentApps.ApplyCredentialValidationAsync(
                agentApp.Id, SlackRuntimeCredentialValidationState.Verified, ct))
                ?.RuntimeCredentialValidationState ?? agentApp.RuntimeCredentialValidationState;
        }

        await RejectCandidateCredentialsAsync(agentApp, ct);
        return agentApp.RuntimeCredentialValidationState;
    }

    private static bool HasRequiredAgentBotScopes(IReadOnlySet<string>? granted) =>
        granted is not null && RequiredAgentBotScopes.All(scope => granted.Contains(scope));

    private async Task<bool> IsUnchangedRuntimeCredentialsAsync(
        string agentAppId,
        string botToken,
        string appLevelToken,
        CancellationToken ct)
    {
        // A pending rotation parks the previous pair in the previous slot;
        // while it exists the runtime addresses are not the verified pair,
        // so a resubmission must never be treated as unchanged.
        if (await HasPreviousSecretsAsync(agentAppId, ct))
            return false;
        var storedBot = await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken), ct);
        var storedApp = await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken), ct);
        return storedBot is not null
            && storedApp is not null
            && CryptographicOperations.FixedTimeEquals(storedBot, Encoding.UTF8.GetBytes(botToken))
            && CryptographicOperations.FixedTimeEquals(storedApp, Encoding.UTF8.GetBytes(appLevelToken));
    }

    private async Task<bool> HasPreviousSecretsAsync(string agentAppId, CancellationToken ct) =>
        await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousBotToken), ct) is not null;

    private async Task PreserveRuntimeSecretsAsync(string agentAppId, CancellationToken ct)
    {
        var bot = await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken), ct);
        var app = await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken), ct);
        if (bot is not null)
            await _secrets.StoreAsync(
                SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousBotToken), bot, ct);
        if (app is not null)
            await _secrets.StoreAsync(
                SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousAppToken), app, ct);
    }

    private async Task RestorePreviousSecretsAsync(string agentAppId, CancellationToken ct)
    {
        var bot = await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousBotToken), ct);
        var app = await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousAppToken), ct);
        if (bot is not null)
            await _secrets.StoreAsync(
                SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken), bot, ct);
        if (app is not null)
            await _secrets.StoreAsync(
                SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken), app, ct);
        await DeletePreviousSecretsAsync(agentAppId, ct);
    }

    private async Task DeletePreviousSecretsAsync(string agentAppId, CancellationToken ct)
    {
        await _secrets.DeleteAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousBotToken), ct);
        await _secrets.DeleteAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousAppToken), ct);
    }

    private async Task RejectCandidateCredentialsAsync(ManagedSlackAgentApp agentApp, CancellationToken ct)
    {
        if (agentApp.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.Verified)
            return;
        await _secrets.DeleteAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentApp.Id, SecretKind.BotToken), ct);
        await _secrets.DeleteAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentApp.Id, SecretKind.AppToken), ct);
        if (agentApp.RuntimeCredentialValidationState is SlackRuntimeCredentialValidationState.Candidate
            or SlackRuntimeCredentialValidationState.AwaitingSocket)
            await _agentApps.ApplyCredentialValidationAsync(agentApp.Id, SlackRuntimeCredentialValidationState.Failed, ct);
    }

    private async Task<ManagedSlackAgentApp> ReloadAsync(string agentAppId, CancellationToken ct) =>
        await _agentApps.GetAsync(agentAppId, ct)
        ?? throw new InvalidOperationException("The Agent App disappeared during the install step.");

    private static SlackInstallAgentProgress Progress(
        AgentConnection connection,
        ManagedSlackAgentApp agentApp,
        string nextAction,
        string? errorClass = null) => new(
        agentApp.EnrollmentId,
        agentApp.WorkspaceTeamId,
        new SlackInstallAgentConnectionState(
            connection.Id,
            connection.ProjectId,
            connection.AgentId,
            connection.WorkspaceTeamId,
            connection.AppId,
            connection.BotUserId,
            connection.DesiredState,
            connection.ConnectionHealth,
            connection.HealthReason,
            connection.SetupProgress),
        new SlackInstallAgentAppState(
            agentApp.Id,
            agentApp.AppId,
            agentApp.BotUserId,
            agentApp.AppLifecycle,
            agentApp.Authorization,
            agentApp.RuntimeCredentialValidationState,
            agentApp.BindingState,
            agentApp.ManifestState,
            agentApp.TransportReadiness,
            agentApp.NextAction,
            agentApp.InstallUrl,
            agentApp.UnknownOutcome,
            agentApp.ErrorClass,
            agentApp.DeletedAt),
        nextAction,
        errorClass);
}

public sealed record SlackInstallAgentProgress(
    string EnrollmentId,
    string WorkspaceTeamId,
    SlackInstallAgentConnectionState Connection,
    SlackInstallAgentAppState AgentApp,
    string NextAction,
    string? ErrorClass = null)
{
    public string? InstallUrl => string.IsNullOrWhiteSpace(AgentApp.InstallUrl) ? null : AgentApp.InstallUrl;
}

public sealed record SlackInstallAgentConnectionState(
    string Id,
    string ProjectId,
    string AgentId,
    string WorkspaceTeamId,
    string AppId,
    string BotUserId,
    string DesiredState,
    string ConnectionHealth,
    string? HealthReason,
    string SetupProgress);

public sealed record SlackInstallAgentAppState(
    string Id,
    string AppId,
    string BotUserId,
    string AppLifecycle,
    string Authorization,
    string RuntimeCredentialValidationState,
    string BindingState,
    string ManifestState,
    string TransportReadiness,
    string NextAction,
    string? InstallUrl,
    string? UnknownOutcome,
    string? ErrorClass,
    DateTimeOffset? DeletedAt);

public sealed record SlackInstallAgentCredentialResult(
    bool Accepted,
    string RuntimeCredentialValidationState,
    string? ErrorClass = null);

public sealed record SlackInstallAgentValidationResult(
    SlackInstallAgentValidationOutcome Outcome,
    SlackAgentAppBindingStatus? Binding = null);

public enum SlackInstallAgentValidationOutcome
{
    Verified,
    AlreadyVerified,
    Mismatch,
}
