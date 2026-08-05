using System.Security.Cryptography;
using System.Text;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Idempotent workspace Mohist App setup orchestration. Each entry reads the
/// current enrollment facts, performs at most one external write per concern,
/// and returns a single progress projection with one next action. Reruns
/// restore or repair the same enrollment / Mohist App rather than creating a
/// second one. Socket validation lease + <c>hello.app_id</c> are driven by
/// the adapter through <see cref="SlackAdapterLeaseService"/>; this service
/// only stages candidate runtime credentials and exposes the resulting state.
/// </summary>
public sealed class SlackManagerSetupOrchestrator : IScopedService
{
    private const string ProductCapabilityVersion = "p0-manager-app";
    private const int ManifestVersion = 2;
    private const string MohistAppName = "Mohist";
    private const string MohistAppDescription = "Mohist workspace management";

    private static readonly string[] ManagerBotScopes =
        ["chat:write", "im:history", "users:read"];

    private readonly ISlackConfigurationCredentialPort _configurationPort;
    private readonly ISlackConfigurationCredentialStore _configurationStore;
    private readonly SlackWorkspaceEnrollmentStore _enrollments;
    private readonly SlackManifestGenerator _manifests;
    private readonly ISlackAppManagementPort _appManagement;
    private readonly ISlackBotIdentityVerificationPort _botIdentity;
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _timeProvider;

    public SlackManagerSetupOrchestrator(
        ISlackConfigurationCredentialPort configurationPort,
        ISlackConfigurationCredentialStore configurationStore,
        SlackWorkspaceEnrollmentStore enrollments,
        SlackManifestGenerator manifests,
        ISlackAppManagementPort appManagement,
        ISlackBotIdentityVerificationPort botIdentity,
        ISecretStore secrets,
        TimeProvider timeProvider)
    {
        _configurationPort = configurationPort;
        _configurationStore = configurationStore;
        _enrollments = enrollments;
        _manifests = manifests;
        _appManagement = appManagement;
        _botIdentity = botIdentity;
        _secrets = secrets;
        _timeProvider = timeProvider;
    }

    public async Task<SlackSetupProgress> SupplyConfigurationAsync(
        SlackSetupConfigurationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceTeamId);
        ArgumentNullException.ThrowIfNull(request.Credentials);
        request.Credentials.Validate();

        var rotation = await _configurationPort.RotateAsync(request.Credentials, ct);
        if (rotation.Outcome != SlackConfigurationCredentialRotationOutcome.Succeeded
            || rotation.Credentials is null
            || string.IsNullOrWhiteSpace(rotation.WorkspaceTeamId)
            || rotation.ExpiresAt is null)
        {
            return Failed(request.WorkspaceTeamId, rotation.Outcome, rotation.ErrorClass);
        }

        if (!string.Equals(rotation.WorkspaceTeamId, request.WorkspaceTeamId.Trim(), StringComparison.Ordinal))
            return Failed(request.WorkspaceTeamId, SlackConfigurationCredentialRotationOutcome.DefiniteFailure, "workspace_mismatch");

        var enrollment = await EnsureEnrollmentAsync(rotation.WorkspaceTeamId, ct);
        var persisted = await _configurationStore.StoreVerifiedRotationAsync(
            enrollment.Id,
            enrollment.WorkspaceTeamId,
            enrollment.ConfigurationCredentialGeneration,
            rotation.Credentials,
            rotation.ExpiresAt.Value,
            _timeProvider.GetUtcNow(),
            ct);
        if (!persisted.Stored)
            return Failed(
                request.WorkspaceTeamId,
                SlackConfigurationCredentialRotationOutcome.DefiniteFailure,
                persisted.ErrorClass);

        await EnsureManagerAppCreatedAsync(enrollment, ct);
        return await ProjectAsync(enrollment.WorkspaceTeamId, ct);
    }

    public async Task<SlackSetupProgress> SupplyRuntimeCredentialsAsync(
        SlackSetupRuntimeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceTeamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BotToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AppLevelToken);

        var enrollment = await _enrollments.GetActiveByTeamAsync(request.WorkspaceTeamId.Trim(), ct)
            ?? throw new SlackManagerConflictException(
                "Run Configuration setup for this workspace before providing runtime credentials.",
                "enrollment_required");
        if (string.IsNullOrWhiteSpace(enrollment.ManagerAppId))
            throw new SlackManagerConflictException(
                "The Mohist App must be created before providing runtime credentials.",
                "manager_app_not_created");

        // A ready enrollment re-supplying the exact verified credentials is a
        // no-op; anything else re-validates and rotates through the same
        // candidate -> Socket hello path as the first provision.
        if (enrollment.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.Verified
            && await IsUnchangedRuntimeCredentialsAsync(enrollment.Id, request, ct))
            return await ProjectAsync(enrollment.WorkspaceTeamId, ct);

        var verified = await _botIdentity.VerifyAsync(new(request.BotToken), ct);
        if (!verified.Verified
            || !string.Equals(verified.WorkspaceTeamId, enrollment.WorkspaceTeamId, StringComparison.Ordinal)
            || !string.Equals(verified.AppId, enrollment.ManagerAppId, StringComparison.Ordinal)
            || verified.BotUserId is null
            || !HasRequiredScopes(verified.GrantedScopes))
        {
            if (await HasPreviousRuntimeSecretsAsync(enrollment.Id, ct))
            {
                // A rotation was in flight; restore the previous verified pair.
                await RestorePreviousRuntimeSecretsAsync(enrollment.Id, ct);
                await _enrollments.CompleteSocketVerificationAsync(enrollment.Id, ct);
            }
            else if (enrollment.RuntimeCredentialValidationState != SlackRuntimeCredentialValidationState.Verified)
            {
                await DeleteCandidateSecretsAsync(enrollment.Id, ct);
            }

            return new(
                enrollment.Id,
                enrollment.WorkspaceTeamId,
                SlackSetupPhase.Failed,
                ManagerAppId: enrollment.ManagerAppId,
                InstallUrl: null,
                NextAction: SlackSetupNextAction.SupplyRuntimeCredentials,
                ErrorClass: "runtime_credential_mismatch");
        }

        if (enrollment.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.Verified)
        {
            // Rotate: park the verified pair in the previous slot before the
            // new candidate overwrites the runtime addresses, so the old
            // credentials survive until the new Socket hello verifies.
            await PreserveRuntimeSecretsAsync(enrollment.Id, ct);
            await StoreRuntimeSecretsAsync(enrollment.Id, request.BotToken, request.AppLevelToken, ct);
            await _enrollments.StageManagerRuntimeCredentialsAsync(enrollment.Id, verified.BotUserId!, ct);
            await _enrollments.ApplySocketValidationAsync(enrollment.Id, SlackRuntimeCredentialValidationState.AwaitingSocket, ct);
        }
        else
        {
            await StoreRuntimeSecretsAsync(enrollment.Id, request.BotToken, request.AppLevelToken, ct);

            if (enrollment.RuntimeCredentialValidationState
                is SlackRuntimeCredentialValidationState.NotProvided
                or SlackRuntimeCredentialValidationState.Candidate
                or SlackRuntimeCredentialValidationState.Failed)
            {
                await _enrollments.StageManagerRuntimeCredentialsAsync(enrollment.Id, verified.BotUserId!, ct);
                await _enrollments.ApplySocketValidationAsync(enrollment.Id, SlackRuntimeCredentialValidationState.AwaitingSocket, ct);
            }
        }

        return await ProjectAsync(enrollment.WorkspaceTeamId, ct);
    }

    public async Task<SlackSetupProgress?> GetProgressAsync(string workspaceTeamId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceTeamId);
        var enrollment = await _enrollments.GetByTeamAsync(workspaceTeamId.Trim(), ct);
        return enrollment is null ? null : await ProjectAsync(enrollment.WorkspaceTeamId, ct);
    }

    private async Task<SlackWorkspaceEnrollment> EnsureEnrollmentAsync(string workspaceTeamId, CancellationToken ct)
    {
        var enrollment = await _enrollments.GetByTeamAsync(workspaceTeamId, ct);
        if (enrollment is not null && enrollment.Lifecycle == SlackEnrollmentLifecycle.Removed)
            throw new SlackManagerConflictException(
                "The workspace enrollment was removed and cannot be reused.",
                "enrollment_removed");

        if (enrollment is null)
        {
            enrollment = new SlackWorkspaceEnrollment
            {
                Id = $"enrollment_{Guid.NewGuid():N}",
                WorkspaceTeamId = workspaceTeamId,
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
                enrollment = await _enrollments.GetByTeamAsync(workspaceTeamId, ct)
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
                enrollment.Id, $"manager_actor_{Guid.NewGuid():N}", ct)
                ?? throw new InvalidOperationException("The workspace enrollment disappeared during setup.");
        return enrollment;
    }

    private async Task EnsureManagerAppCreatedAsync(SlackWorkspaceEnrollment enrollment, CancellationToken ct)
    {
        if (enrollment.ManagerAppLifecycle == SlackManagerAppLifecycle.Created
            && !string.IsNullOrWhiteSpace(enrollment.ManagerAppId))
            return;

        var manifest = _manifests.Generate(new SlackManifestInput(
            MohistAppName,
            MohistAppDescription,
            ProductCapabilityVersion,
            new SlackManifestIdentitySnapshot(string.Empty, string.Empty, enrollment.WorkspaceTeamId),
            SlackManifestKind.MohistApp,
            ManifestVersion));

        if (enrollment.ManagerAppLifecycle == SlackManagerAppLifecycle.CreateUnknown)
            return;

        var begin = await _enrollments.BeginManagerAppCreateAsync(
            enrollment.Id, enrollment.ManagerAppOperationFence, $"manager_create_{Guid.NewGuid():N}", ct);
        if (!begin.Accepted)
        {
            var current = begin.Enrollment;
            if (current is not null
                && (current.ManagerAppLifecycle == SlackManagerAppLifecycle.Creating
                    || current.ManagerAppLifecycle == SlackManagerAppLifecycle.Created
                    && string.IsNullOrWhiteSpace(current.ManagerAppId)))
            {
                await _enrollments.RecoverManagerAppCreateAsync(
                    enrollment.Id,
                    current.ManagerAppOperationFence,
                    SlackSecretRedactor.Redact("interrupted_create"),
                    ct);
            }
            return;
        }

        var request = new SlackAppManagementRequest(enrollment.Id, enrollment.Id, enrollment.WorkspaceTeamId, ManifestJson: manifest.CanonicalJson);
        var external = await _appManagement.CreateAsync(request, ct);
        var fence = begin.Enrollment!.ManagerAppOperationFence;
        if (external.Outcome == SlackAppManagementOutcome.Succeeded && external.AppId is not null && external.InstallUrl is not null)
        {
            await _enrollments.ApplyManagerAppCreateResultAsync(
                enrollment.Id, fence, SlackManagerAppLifecycle.Created, "created", ct);
            await _enrollments.RecordManagerAppCreatedAsync(
                enrollment.Id, external.AppId, manifest.Hash, external.InstallUrl, ct);
            await StoreManagerAppSecretsAsync(enrollment.Id, external.ClientSecret, external.SigningSecret, ct);
        }
        else if (external.Outcome == SlackAppManagementOutcome.Succeeded && external.AppId is not null)
        {
            await _enrollments.ApplyManagerAppCreateResultAsync(
                enrollment.Id, fence, SlackManagerAppLifecycle.CreateUnknown,
                SlackSecretRedactor.Redact(external.ErrorMessage ?? external.ErrorClass ?? "install_url_missing"), ct);
            await _enrollments.RecordManagerAppIdentityAsync(enrollment.Id, external.AppId, ct);
        }
        else if (external.Outcome == SlackAppManagementOutcome.Unknown)
        {
            await _enrollments.ApplyManagerAppCreateResultAsync(
                enrollment.Id, fence, SlackManagerAppLifecycle.CreateUnknown,
                SlackSecretRedactor.Redact(external.ErrorMessage ?? external.ErrorClass ?? "unknown"), ct);
        }
        else
        {
            await _enrollments.ApplyManagerAppCreateResultAsync(
                enrollment.Id, fence, SlackManagerAppLifecycle.NotCreated,
                SlackSecretRedactor.Redact(external.ErrorMessage ?? external.ErrorClass ?? "definite_failure"), ct);
        }
    }

    private async Task StoreManagerAppSecretsAsync(
        string enrollmentId, string? clientSecret, string? signingSecret, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(clientSecret))
            await _secrets.StoreAsync(
                SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.ClientSecret),
                Encoding.UTF8.GetBytes(clientSecret), ct);
        if (!string.IsNullOrEmpty(signingSecret))
            await _secrets.StoreAsync(
                SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.SigningSecret),
                Encoding.UTF8.GetBytes(signingSecret), ct);
    }

    private async Task StoreRuntimeSecretsAsync(
        string enrollmentId, string botToken, string appLevelToken, CancellationToken ct)
    {
        await _secrets.StoreAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.BotToken),
            Encoding.UTF8.GetBytes(botToken.Trim()), ct);
        await _secrets.StoreAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.AppToken),
            Encoding.UTF8.GetBytes(appLevelToken.Trim()), ct);
    }

    private async Task DeleteCandidateSecretsAsync(string enrollmentId, CancellationToken ct)
    {
        await _secrets.DeleteAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.AppToken), ct);
        await _secrets.DeleteAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.BotToken), ct);
    }

    private async Task<bool> IsUnchangedRuntimeCredentialsAsync(
        string enrollmentId,
        SlackSetupRuntimeRequest request,
        CancellationToken ct)
    {
        // A pending rotation parks the previous pair in the previous slot;
        // while it exists the runtime addresses are not the verified pair,
        // so a resubmission must never be treated as unchanged.
        if (await HasPreviousRuntimeSecretsAsync(enrollmentId, ct))
            return false;
        var storedBot = await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.BotToken), ct);
        var storedApp = await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.AppToken), ct);
        return storedBot is not null
            && storedApp is not null
            && CryptographicOperations.FixedTimeEquals(storedBot, Encoding.UTF8.GetBytes(request.BotToken.Trim()))
            && CryptographicOperations.FixedTimeEquals(storedApp, Encoding.UTF8.GetBytes(request.AppLevelToken.Trim()));
    }

    private async Task<bool> HasPreviousRuntimeSecretsAsync(string enrollmentId, CancellationToken ct) =>
        await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousBotToken), ct) is not null;

    private async Task PreserveRuntimeSecretsAsync(string enrollmentId, CancellationToken ct)
    {
        var bot = await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.BotToken), ct);
        var app = await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.AppToken), ct);
        if (bot is not null)
            await _secrets.StoreAsync(
                SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousBotToken), bot, ct);
        if (app is not null)
            await _secrets.StoreAsync(
                SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousAppToken), app, ct);
    }

    private async Task RestorePreviousRuntimeSecretsAsync(string enrollmentId, CancellationToken ct)
    {
        var bot = await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousBotToken), ct);
        var app = await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousAppToken), ct);
        if (bot is not null)
            await _secrets.StoreAsync(
                SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.BotToken), bot, ct);
        if (app is not null)
            await _secrets.StoreAsync(
                SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.AppToken), app, ct);
        await _secrets.DeleteAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousBotToken), ct);
        await _secrets.DeleteAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousAppToken), ct);
    }

    private static bool HasRequiredScopes(IReadOnlySet<string>? granted) =>
        granted is not null && ManagerBotScopes.All(scope => granted.Contains(scope));

    private async Task<SlackSetupProgress> ProjectAsync(string workspaceTeamId, CancellationToken ct)
    {
        var enrollment = await _enrollments.GetByTeamAsync(workspaceTeamId, ct);
        if (enrollment is null)
            return new(null, workspaceTeamId, SlackSetupPhase.NotStarted, null, null, SlackSetupNextAction.SupplyConfiguration, null);
        return Derive(enrollment);
    }

    private static SlackSetupProgress Derive(SlackWorkspaceEnrollment enrollment)
    {
        var (phase, nextAction, errorClass) = DerivePhase(enrollment);
        return new(
            enrollment.Id,
            enrollment.WorkspaceTeamId,
            phase,
            string.IsNullOrWhiteSpace(enrollment.ManagerAppId) ? null : enrollment.ManagerAppId,
            string.IsNullOrWhiteSpace(enrollment.ManagerAppInstallUrl) ? null : enrollment.ManagerAppInstallUrl,
            nextAction,
            errorClass);
    }

    private static (string Phase, string NextAction, string? ErrorClass) DerivePhase(SlackWorkspaceEnrollment enrollment)
    {
        if (enrollment.Lifecycle == SlackEnrollmentLifecycle.Removed)
            return (SlackSetupPhase.NotStarted, SlackSetupNextAction.SupplyConfiguration, null);
        if (string.IsNullOrWhiteSpace(enrollment.ConfigurationCredentialRef))
            return (SlackSetupPhase.ConfigurationRequired, SlackSetupNextAction.SupplyConfiguration, null);
        if (enrollment.ManagerAppLifecycle == SlackManagerAppLifecycle.CreateUnknown)
            return (SlackSetupPhase.CreateUnknown, SlackSetupNextAction.ReconcileCreate, enrollment.ManagerAppOperationOutcome);
        if (string.IsNullOrWhiteSpace(enrollment.ManagerAppId))
            return (SlackSetupPhase.AwaitingInstall, SlackSetupNextAction.SupplyConfiguration, null);
        if (enrollment.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.Verified)
            return (SlackSetupPhase.Ready, SlackSetupNextAction.Ready, null);
        if (enrollment.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.Failed)
            return (SlackSetupPhase.Failed, SlackSetupNextAction.SupplyRuntimeCredentials, null);
        if (enrollment.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.NotProvided)
            return (SlackSetupPhase.AwaitingInstall, SlackSetupNextAction.SupplyRuntimeCredentials, null);
        return (SlackSetupPhase.AwaitingSocketValidation, SlackSetupNextAction.ReportSocketHello, null);
    }

    private static SlackSetupProgress Failed(
        string workspaceTeamId,
        SlackConfigurationCredentialRotationOutcome outcome,
        string? errorClass)
    {
        var phase = outcome == SlackConfigurationCredentialRotationOutcome.Unknown
            ? SlackSetupPhase.ConfigurationUnknown
            : SlackSetupPhase.Failed;
        return new(null, workspaceTeamId, phase, null, null, SlackSetupNextAction.SupplyConfiguration, errorClass);
    }
}

public sealed record SlackSetupConfigurationRequest(
    string WorkspaceTeamId,
    SlackConfigurationCredentialPair Credentials);

public sealed record SlackSetupRuntimeRequest(
    string WorkspaceTeamId,
    string BotToken,
    string AppLevelToken);

public sealed record SlackSetupProgress(
    string? EnrollmentId,
    string WorkspaceTeamId,
    string Phase,
    string? ManagerAppId,
    string? InstallUrl,
    string NextAction,
    string? ErrorClass);

public static class SlackSetupPhase
{
    public const string NotStarted = "not_started";
    public const string ConfigurationRequired = "configuration_required";
    public const string ConfigurationUnknown = "configuration_unknown";
    public const string CreateUnknown = "create_unknown";
    public const string AwaitingInstall = "awaiting_install";
    public const string AwaitingSocketValidation = "awaiting_socket_validation";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

public static class SlackSetupNextAction
{
    public const string SupplyConfiguration = "supply_configuration";
    public const string SupplyRuntimeCredentials = "supply_runtime_credentials";
    public const string ReportSocketHello = "report_socket_hello";
    public const string ReconcileCreate = "reconcile_create";
    public const string Ready = "ready";
}
