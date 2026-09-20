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
/// and returns a single progress projection with one user-facing primary
/// action. Reruns restore or repair the same enrollment / Mohist App rather
/// than creating a second one. Socket validation lease + <c>hello.app_id</c>
/// are driven by the adapter through <see cref="SlackAdapterLeaseService"/>;
/// this service only stages candidate runtime credentials and exposes the
/// resulting state.
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
    private readonly ISlackAppManagementFactPort _appManagementFacts;
    private readonly ISlackBotIdentityVerificationPort _botIdentity;
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _timeProvider;

    public SlackManagerSetupOrchestrator(
        ISlackConfigurationCredentialPort configurationPort,
        ISlackConfigurationCredentialStore configurationStore,
        SlackWorkspaceEnrollmentStore enrollments,
        SlackManifestGenerator manifests,
        ISlackAppManagementPort appManagement,
        ISlackAppManagementFactPort appManagementFacts,
        ISlackBotIdentityVerificationPort botIdentity,
        ISecretStore secrets,
        TimeProvider timeProvider)
    {
        _configurationPort = configurationPort;
        _configurationStore = configurationStore;
        _enrollments = enrollments;
        _manifests = manifests;
        _appManagement = appManagement;
        _appManagementFacts = appManagementFacts;
        _botIdentity = botIdentity;
        _secrets = secrets;
        _timeProvider = timeProvider;
    }

    public async Task<SlackSetupProgress> SupplyConfigurationAsync(
        SlackSetupConfigurationRequest request,
        string? workspaceTeamId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Credentials);
        request.Credentials.Validate();

        var selected = await SelectEnrollmentAsync(workspaceTeamId, ct);
        var rotation = await _configurationPort.RotateAsync(request.Credentials, ct);
        if (rotation.Outcome != SlackConfigurationCredentialRotationOutcome.Succeeded
            || rotation.Credentials is null
            || string.IsNullOrWhiteSpace(rotation.WorkspaceTeamId)
            || rotation.ExpiresAt is null)
        {
            return Failed(
                rotation.WorkspaceTeamId ?? selected?.WorkspaceTeamId,
                rotation.Outcome,
                rotation.ErrorClass);
        }

        // A selector binds the submission before the write: the pair must
        // verify against the selected Enrollment's team, so credentials for
        // another Workspace can never land on it.
        if (selected is not null
            && !string.Equals(rotation.WorkspaceTeamId, selected.WorkspaceTeamId, StringComparison.Ordinal))
        {
            return new(
                selected.Id,
                selected.WorkspaceTeamId,
                SlackSetupPhase.Failed,
                AppIdOrNull(selected),
                InstallUrl: null,
                SlackSetupPrimaryAction.SupplyConfiguration,
                Summarize(selected, SlackSetupPhase.Failed),
                "configuration_workspace_mismatch");
        }

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
                rotation.WorkspaceTeamId,
                SlackConfigurationCredentialRotationOutcome.DefiniteFailure,
                persisted.ErrorClass);

        return await AdvanceManagerAppAsync(enrollment, reconcileUnknown: true, ct);
    }

    public async Task<SlackSetupProgress> SupplyRuntimeCredentialsAsync(
        SlackSetupRuntimeRequest request,
        string? workspaceTeamId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BotToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AppLevelToken);

        var selected = await SelectEnrollmentAsync(workspaceTeamId, ct);
        // A runtime pair carries no team identity of its own, so the target is
        // resolved before any verification: the selected Enrollment or,
        // without a selector, the only eligible one. Verification never
        // redirects a write to another Enrollment.
        var enrollment = selected ?? await RequireSingleEnrollmentAsync(
            "Run Configuration setup for this workspace before providing runtime credentials.", ct);

        var verified = await _botIdentity.VerifyAsync(new(request.BotToken), ct);
        if (!verified.Verified
            || string.IsNullOrWhiteSpace(verified.WorkspaceTeamId)
            || string.IsNullOrWhiteSpace(verified.BotUserId))
        {
            return new(
                enrollment.Id,
                enrollment.WorkspaceTeamId,
                SlackSetupPhase.Failed,
                AppIdOrNull(enrollment),
                null,
                SlackSetupPrimaryAction.SupplyRuntimeCredentials,
                Summarize(enrollment, SlackSetupPhase.Failed),
                verified.ErrorClass ?? "runtime_credential_mismatch");
        }

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

        if (!string.Equals(verified.WorkspaceTeamId, enrollment.WorkspaceTeamId, StringComparison.Ordinal)
            || !string.Equals(verified.AppId, enrollment.ManagerAppId, StringComparison.Ordinal)
            || !HasRequiredScopes(verified.GrantedScopes))
        {
            if (await HasPreviousRuntimeSecretsAsync(enrollment.Id, ct))
            {
                // A rotation was in flight; drop the failed candidate and
                // restore the previous verified pair.
                await DeleteCandidateSecretsAsync(enrollment.Id, ct);
                await RestorePreviousRuntimeSecretsAsync(enrollment.Id, ct);
                await _enrollments.CompleteSocketVerificationAsync(enrollment.Id, ct);
            }
            else if (enrollment.RuntimeCredentialValidationState
                is SlackRuntimeCredentialValidationState.Candidate
                or SlackRuntimeCredentialValidationState.AwaitingSocket)
            {
                await DeleteCandidateSecretsAsync(enrollment.Id, ct);
                await _enrollments.ApplySocketValidationAsync(enrollment.Id, SlackRuntimeCredentialValidationState.Failed, ct);
            }

            return new(
                enrollment.Id,
                enrollment.WorkspaceTeamId,
                SlackSetupPhase.Failed,
                AppIdOrNull(enrollment),
                InstallUrl: null,
                SlackSetupPrimaryAction.SupplyRuntimeCredentials,
                Summarize(enrollment, SlackSetupPhase.Failed),
                ErrorClass: "runtime_credential_mismatch");
        }

        if (enrollment.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.Verified)
        {
            // Rotation: the runtime addresses keep serving the verified pair
            // while the new pair waits as an unverified candidate. Preserve the
            // verified pair for recovery, stage Verified -> Candidate, then
            // store the new pair at the candidate addresses and advance to
            // AwaitingSocket. The runtime lease is closed from the moment the
            // state leaves Verified until a matching Socket hello promotes the
            // candidate, so it can never serve the unverified pair, and the old
            // pair stays restorable from Previous at every boundary.
            await PreserveRuntimeSecretsAsync(enrollment.Id, ct);
            await _enrollments.StageManagerRuntimeCredentialsAsync(enrollment.Id, verified.BotUserId, ct);
            await StoreCandidateSecretsAsync(enrollment.Id, request.BotToken, request.AppLevelToken, ct);
            await _enrollments.ApplySocketValidationAsync(enrollment.Id, SlackRuntimeCredentialValidationState.AwaitingSocket, ct);
        }
        else
        {
            // Not Verified: the runtime lease is already closed. A
            // Candidate/AwaitingSocket state with a parked Previous is a
            // resumed rotation: keep the non-Verified state, (re)store the
            // candidate, and only push Candidate -> AwaitingSocket (never stage
            // back from AwaitingSocket).
            await StoreCandidateSecretsAsync(enrollment.Id, request.BotToken, request.AppLevelToken, ct);

            if (enrollment.RuntimeCredentialValidationState
                is SlackRuntimeCredentialValidationState.NotProvided
                or SlackRuntimeCredentialValidationState.Candidate
                or SlackRuntimeCredentialValidationState.Failed)
            {
                await _enrollments.StageManagerRuntimeCredentialsAsync(enrollment.Id, verified.BotUserId, ct);
                await _enrollments.ApplySocketValidationAsync(enrollment.Id, SlackRuntimeCredentialValidationState.AwaitingSocket, ct);
            }
        }

        return await ProjectAsync(enrollment.WorkspaceTeamId, ct);
    }

    public async Task<SlackSetupProgress?> GetProgressAsync(
        string? workspaceTeamId = null,
        CancellationToken ct = default)
    {
        var selected = await SelectEnrollmentAsync(workspaceTeamId, ct);
        if (selected is not null)
            return await ProjectAsync(selected.WorkspaceTeamId, ct);

        var enrollments = await _enrollments.ListActiveAsync(ct);
        if (enrollments.Count == 0)
            return null;
        if (enrollments.Count > 1)
            throw AmbiguousWorkspace(enrollments);
        return await ProjectAsync(enrollments[0].WorkspaceTeamId, ct);
    }

    public async Task<SlackSetupProgress> ResumeAsync(
        string? workspaceTeamId = null,
        CancellationToken ct = default)
    {
        var selected = await SelectEnrollmentAsync(workspaceTeamId, ct)
            ?? await RequireSingleEnrollmentAsync(
                "Run Slack setup with Configuration credentials first.", ct);
        return await AdvanceManagerAppAsync(selected, reconcileUnknown: true, ct);
    }

    /// <summary>
    /// Resolves an explicit <c>--workspace-team</c> selector to its active
    /// Enrollment before any external write. A selector naming an unknown or
    /// ineligible Enrollment fails instead of falling back to another record.
    /// </summary>
    private async Task<SlackWorkspaceEnrollment?> SelectEnrollmentAsync(
        string? workspaceTeamId,
        CancellationToken ct)
    {
        var selector = workspaceTeamId?.Trim();
        if (string.IsNullOrEmpty(selector))
            return null;
        return await _enrollments.GetActiveByTeamAsync(selector, ct)
            ?? throw new SlackManagerConflictException(
                $"No active Slack workspace enrollment matches team {selector}.",
                "workspace_not_enrolled");
    }

    private async Task<SlackWorkspaceEnrollment> RequireSingleEnrollmentAsync(
        string missingEnrollmentMessage,
        CancellationToken ct)
    {
        var active = await _enrollments.ListActiveAsync(ct);
        if (active.Count == 0)
            throw new SlackManagerConflictException(missingEnrollmentMessage, "enrollment_required");
        if (active.Count > 1)
            throw AmbiguousWorkspace(active);
        return active[0];
    }

    /// <summary>
    /// Several enrolled Workspaces and no selector leave no safe target, so the
    /// operation fails closed and names the real choices. Nothing here picks
    /// the first record a list returns.
    /// </summary>
    private SlackManagerConflictException AmbiguousWorkspace(
        IReadOnlyList<SlackWorkspaceEnrollment> enrollments)
    {
        var choices = enrollments
            .Select(enrollment => new SlackSetupWorkspaceChoice(
                enrollment.WorkspaceTeamId,
                $"Slack workspace {enrollment.WorkspaceTeamId}",
                DerivePhase(enrollment, DesiredManifestHash(enrollment.WorkspaceTeamId)).Phase))
            .ToList();
        return new SlackManagerConflictException(
            "More than one Slack workspace is enrolled; select one with --workspace-team <team-id>.",
            "workspace_selection_required",
            choices);
    }

    private async Task<SlackSetupProgress> AdvanceManagerAppAsync(
        SlackWorkspaceEnrollment enrollment,
        bool reconcileUnknown,
        CancellationToken ct)
    {
        await EnsureManagerAppCreatedAsync(enrollment, ct);
        enrollment = await ReloadEnrollmentAsync(enrollment.Id, ct);

        string? errorClass = null;
        if (reconcileUnknown && enrollment.ManagerAppLifecycle == SlackManagerAppLifecycle.CreateUnknown)
        {
            var reconciliation = await ReconcileManagerAppCreateAsync(enrollment, ct);
            errorClass = reconciliation.ErrorClass;
            enrollment = await ReloadEnrollmentAsync(enrollment.Id, ct);
            if (reconciliation.Absent)
            {
                await EnsureManagerAppCreatedAsync(enrollment, ct);
                enrollment = await ReloadEnrollmentAsync(enrollment.Id, ct);
            }
        }

        var manifestError = await EnsureManagerAppManifestAsync(enrollment, ct);
        errorClass ??= manifestError;
        var progress = await ProjectAsync(enrollment.WorkspaceTeamId, ct);
        return errorClass is null ? progress : progress with { ErrorClass = errorClass };
    }

    private async Task<SlackWorkspaceEnrollment> ReloadEnrollmentAsync(string enrollmentId, CancellationToken ct) =>
        await _enrollments.GetAsync(enrollmentId, ct)
        ?? throw new InvalidOperationException("The workspace enrollment disappeared during setup.");

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
        if (external.Outcome == SlackAppManagementOutcome.Succeeded && external.AppId is not null)
        {
            var installUrl = string.IsNullOrWhiteSpace(external.InstallUrl)
                ? $"https://api.slack.com/apps/{Uri.EscapeDataString(external.AppId)}/oauth"
                : external.InstallUrl;
            await _enrollments.ApplyManagerAppCreateResultAsync(
                enrollment.Id, fence, SlackManagerAppLifecycle.Created, "created", ct);
            await _enrollments.RecordManagerAppCreatedAsync(
                enrollment.Id, external.AppId, manifest.Hash, installUrl, ct);
            await StoreManagerAppSecretsAsync(enrollment.Id, external.ClientSecret, external.SigningSecret, ct);
        }
        else if (external.Outcome == SlackAppManagementOutcome.Unknown)
        {
            await _enrollments.ApplyManagerAppCreateResultAsync(
                enrollment.Id, fence, SlackManagerAppLifecycle.CreateUnknown,
                SlackSecretRedactor.Redact(external.ErrorMessage ?? external.ErrorClass ?? "unknown"), ct);
            if (!string.IsNullOrWhiteSpace(external.AppId))
                await _enrollments.RecordManagerAppIdentityAsync(enrollment.Id, external.AppId, ct);
        }
        else
        {
            await _enrollments.ApplyManagerAppCreateResultAsync(
                enrollment.Id, fence, SlackManagerAppLifecycle.NotCreated,
                SlackSecretRedactor.Redact(external.ErrorMessage ?? external.ErrorClass ?? "definite_failure"), ct);
        }
    }

    private async Task<ManagerAppCreateReconciliation> ReconcileManagerAppCreateAsync(
        SlackWorkspaceEnrollment enrollment,
        CancellationToken ct)
    {
        if (enrollment.ManagerAppLifecycle != SlackManagerAppLifecycle.CreateUnknown)
            return new(false, null);
        if (string.IsNullOrWhiteSpace(enrollment.ManagerAppId))
            return new(false, enrollment.ManagerAppOperationOutcome ?? "manual_adjudication_required");

        var begin = await _enrollments.BeginManagerAppCreateAsync(
            enrollment.Id,
            enrollment.ManagerAppOperationFence,
            $"manager_reconcile_create_{Guid.NewGuid():N}",
            ct);
        if (!begin.Accepted)
            return new(false, "setup_changed_concurrently");

        var fence = begin.Enrollment!.ManagerAppOperationFence;
        var fact = await _appManagementFacts.InspectAsync(new SlackAppManagementRequest(
            enrollment.Id,
            enrollment.Id,
            enrollment.WorkspaceTeamId,
            enrollment.ManagerAppId), ct);

        if (fact.Outcome == SlackAppManagementFactOutcome.Present
            && !string.IsNullOrWhiteSpace(fact.AppId)
            && string.Equals(fact.AppId, enrollment.ManagerAppId, StringComparison.Ordinal))
        {
            await _enrollments.ApplyManagerAppCreateResultAsync(
                enrollment.Id, fence, SlackManagerAppLifecycle.Created, "reconciled_present", ct);
            await _enrollments.RecordReconciledManagerAppAsync(
                enrollment.Id,
                fact.AppId,
                $"https://api.slack.com/apps/{Uri.EscapeDataString(fact.AppId)}/oauth",
                ct);
            return new(false, null);
        }

        if (fact.Outcome == SlackAppManagementFactOutcome.Absent)
        {
            await _enrollments.ApplyManagerAppCreateResultAsync(
                enrollment.Id, fence, SlackManagerAppLifecycle.NotCreated, "reconciled_absent", ct);
            return new(true, null);
        }

        var errorClass = fact.ErrorClass ?? "manual_adjudication_required";
        await _enrollments.ApplyManagerAppCreateResultAsync(
            enrollment.Id,
            fence,
            SlackManagerAppLifecycle.CreateUnknown,
            SlackSecretRedactor.Redact(fact.ErrorMessage ?? errorClass),
            ct);
        return new(false, errorClass);
    }

    private async Task<string?> EnsureManagerAppManifestAsync(
        SlackWorkspaceEnrollment enrollment,
        CancellationToken ct)
    {
        if (enrollment.ManagerAppLifecycle != SlackManagerAppLifecycle.Created
            || string.IsNullOrWhiteSpace(enrollment.ManagerAppId))
            return null;

        var manifest = _manifests.Generate(new SlackManifestInput(
            MohistAppName,
            MohistAppDescription,
            ProductCapabilityVersion,
            new SlackManifestIdentitySnapshot(string.Empty, string.Empty, enrollment.WorkspaceTeamId),
            SlackManifestKind.MohistApp,
            ManifestVersion));
        if (string.Equals(enrollment.ManagerAppManifestHash, manifest.Hash, StringComparison.Ordinal))
            return null;

        var result = await _appManagement.UpdateManifestAsync(
            new SlackAppManifestRequest(
                new SlackAppManagementRequest(
                    enrollment.Id,
                    enrollment.Id,
                    enrollment.WorkspaceTeamId,
                    enrollment.ManagerAppId),
                manifest), ct);
        if (result.Outcome == SlackAppManagementOutcome.Succeeded)
        {
            await _enrollments.RecordManagerAppManifestAppliedAsync(enrollment.Id, manifest.Hash, ct);
            return null;
        }
        return result.ErrorClass ?? "manifest_update_failed";
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

    private async Task StoreCandidateSecretsAsync(
        string enrollmentId, string botToken, string appLevelToken, CancellationToken ct)
    {
        await _secrets.StoreAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateBotToken),
            Encoding.UTF8.GetBytes(botToken.Trim()), ct);
        await _secrets.StoreAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateAppToken),
            Encoding.UTF8.GetBytes(appLevelToken.Trim()), ct);
    }

    private async Task DeleteCandidateSecretsAsync(string enrollmentId, CancellationToken ct)
    {
        await _secrets.DeleteAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateAppToken), ct);
        await _secrets.DeleteAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateBotToken), ct);
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

    private static string? AppIdOrNull(SlackWorkspaceEnrollment enrollment) =>
        string.IsNullOrWhiteSpace(enrollment.ManagerAppId) ? null : enrollment.ManagerAppId;

    private string DesiredManifestHash(string workspaceTeamId) => _manifests.Generate(new SlackManifestInput(
        MohistAppName,
        MohistAppDescription,
        ProductCapabilityVersion,
        new SlackManifestIdentitySnapshot(string.Empty, string.Empty, workspaceTeamId),
        SlackManifestKind.MohistApp,
        ManifestVersion)).Hash;

    private async Task<SlackSetupProgress> ProjectAsync(string workspaceTeamId, CancellationToken ct)
    {
        var enrollment = await _enrollments.GetByTeamAsync(workspaceTeamId, ct);
        if (enrollment is null)
            return new(
                null,
                workspaceTeamId,
                SlackSetupPhase.NotStarted,
                null,
                null,
                SlackSetupPrimaryAction.SupplyConfiguration,
                Summarize(null, SlackSetupPhase.NotStarted),
                null);
        return Derive(enrollment, DesiredManifestHash(enrollment.WorkspaceTeamId));
    }

    private static SlackSetupProgress Derive(
        SlackWorkspaceEnrollment enrollment,
        string desiredManifestHash)
    {
        var (phase, primaryAction, errorClass) = DerivePhase(enrollment, desiredManifestHash);
        return new(
            enrollment.Id,
            enrollment.WorkspaceTeamId,
            phase,
            AppIdOrNull(enrollment),
            string.IsNullOrWhiteSpace(enrollment.ManagerAppInstallUrl) ? null : enrollment.ManagerAppInstallUrl,
            primaryAction,
            Summarize(enrollment, phase),
            errorClass);
    }

    private static (string Phase, string PrimaryAction, string? ErrorClass) DerivePhase(
        SlackWorkspaceEnrollment enrollment,
        string desiredManifestHash)
    {
        if (enrollment.Lifecycle == SlackEnrollmentLifecycle.Removed)
            return (SlackSetupPhase.NotStarted, SlackSetupPrimaryAction.SupplyConfiguration, null);
        if (string.IsNullOrWhiteSpace(enrollment.ConfigurationCredentialRef))
            return (SlackSetupPhase.ConfigurationRequired, SlackSetupPrimaryAction.SupplyConfiguration, null);
        if (enrollment.ManagerAppLifecycle is SlackManagerAppLifecycle.CreateUnknown
            or SlackManagerAppLifecycle.Creating
            || enrollment.ManagerAppLifecycle == SlackManagerAppLifecycle.Created
            && string.IsNullOrWhiteSpace(enrollment.ManagerAppId))
            return (SlackSetupPhase.CreateUnknown, SlackSetupPrimaryAction.RerunSetup, enrollment.ManagerAppOperationOutcome);
        if (enrollment.ManagerAppLifecycle == SlackManagerAppLifecycle.NotCreated)
            return (SlackSetupPhase.Failed, SlackSetupPrimaryAction.RerunSetup, enrollment.ManagerAppOperationOutcome);
        if (string.IsNullOrWhiteSpace(enrollment.ManagerAppId))
            return (SlackSetupPhase.CreateUnknown, SlackSetupPrimaryAction.RerunSetup, enrollment.ManagerAppOperationOutcome);
        if (!string.Equals(enrollment.ManagerAppManifestHash, desiredManifestHash, StringComparison.Ordinal))
            return (SlackSetupPhase.ApplyingManifest, SlackSetupPrimaryAction.RerunSetup, null);
        if (enrollment.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.Verified)
            return (SlackSetupPhase.Ready, SlackSetupPrimaryAction.Ready, null);
        if (enrollment.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.Failed)
            return (SlackSetupPhase.Failed, SlackSetupPrimaryAction.SupplyRuntimeCredentials, null);
        if (enrollment.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.NotProvided)
            return (SlackSetupPhase.AwaitingInstall, SlackSetupPrimaryAction.ApproveInstall, null);
        return (SlackSetupPhase.AwaitingSocketValidation, SlackSetupPrimaryAction.AwaitSocketVerification, null);
    }

    /// <summary>
    /// One human sentence naming the target and what the current state means.
    /// Internal App-level steps (create, manifest, Socket hello) stay out of
    /// it: they are Server work, never a task the user is asked to perform.
    /// </summary>
    private static string Summarize(SlackWorkspaceEnrollment? enrollment, string phase)
    {
        var sentence = phase switch
        {
            SlackSetupPhase.NotStarted => "Slack setup has not started for this Workspace.",
            SlackSetupPhase.ConfigurationRequired => "The Workspace Configuration credentials are required.",
            SlackSetupPhase.ConfigurationUnknown => "The Configuration credential rotation result is unknown.",
            SlackSetupPhase.CreateUnknown => "The Mohist App create result is unknown and must be reconciled.",
            SlackSetupPhase.ApplyingManifest => "The Mohist App manifest is out of date.",
            SlackSetupPhase.AwaitingInstall => "The Mohist App installation needs approval in Slack.",
            SlackSetupPhase.AwaitingSocketValidation => "The Mohist App Socket identity is being verified.",
            SlackSetupPhase.Ready => "The Mohist App is ready in this Workspace.",
            _ => "The last setup step failed.",
        };
        return enrollment is null
            ? sentence
            : $"Workspace {enrollment.WorkspaceTeamId}: {sentence}";
    }

    private static SlackSetupProgress Failed(
        string? workspaceTeamId,
        SlackConfigurationCredentialRotationOutcome outcome,
        string? errorClass)
    {
        var phase = outcome == SlackConfigurationCredentialRotationOutcome.Unknown
            ? SlackSetupPhase.ConfigurationUnknown
            : SlackSetupPhase.Failed;
        return new(
            null,
            workspaceTeamId ?? string.Empty,
            phase,
            null,
            null,
            SlackSetupPrimaryAction.SupplyConfiguration,
            Summarize(null, phase),
            errorClass);
    }

    private sealed record ManagerAppCreateReconciliation(bool Absent, string? ErrorClass);
}

public sealed record SlackSetupConfigurationRequest(
    SlackConfigurationCredentialPair Credentials);

public sealed record SlackSetupRuntimeRequest(
    string BotToken,
    string AppLevelToken);

public sealed record SlackSetupProgress(
    string? EnrollmentId,
    string WorkspaceTeamId,
    string Phase,
    string? ManagerAppId,
    string? InstallUrl,
    string PrimaryAction,
    string Summary,
    string? ErrorClass = null);

/// <summary>
/// One enrolled Workspace offered as a setup choice. The team id is the
/// selector value; the name and phase come from durable enrollment facts,
/// because no Slack display name is persisted.
/// </summary>
public sealed record SlackSetupWorkspaceChoice(
    string TeamId,
    string Name,
    string Phase);

public static class SlackSetupPhase
{
    public const string NotStarted = "not_started";
    public const string ConfigurationRequired = "configuration_required";
    public const string ConfigurationUnknown = "configuration_unknown";
    public const string CreateUnknown = "create_unknown";
    public const string AwaitingInstall = "awaiting_install";
    public const string ApplyingManifest = "applying_manifest";
    public const string AwaitingSocketValidation = "awaiting_socket_validation";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

/// <summary>
/// The user-facing primary actions of the setup projection. Every value is one
/// step the caller can execute; Server-internal protocol work such as
/// reporting a Socket hello or reconciling an App create is projected as a
/// rerun of the guide instead of a human task.
/// </summary>
public static class SlackSetupPrimaryAction
{
    public const string SupplyConfiguration = "supply_configuration";
    public const string ApproveInstall = "approve_install";
    public const string SupplyRuntimeCredentials = "supply_runtime_credentials";
    public const string AwaitSocketVerification = "await_socket_verification";
    public const string RerunSetup = "rerun_setup";
    public const string Ready = "ready";
}
