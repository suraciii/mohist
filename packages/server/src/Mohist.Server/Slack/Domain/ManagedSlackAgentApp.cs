namespace Mohist.Server.Slack.Domain;

public sealed class ManagedSlackAgentApp
{
    public string Id { get; set; } = string.Empty;
    public string EnrollmentId { get; set; } = string.Empty;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string AgentConnectionId { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    public string BotUserId { get; set; } = string.Empty;

    public string AppLifecycle { get; set; } = SlackAppLifecycle.NotCreated;
    public string Authorization { get; set; } = SlackAuthorizationState.NotStarted;
    public string ManifestState => ManagedSlackAgentAppStatusDeriver.DeriveManifestState(this);
    public string TransportReadiness => ManagedSlackAgentAppStatusDeriver.DeriveTransportReadiness(this);
    public string NextAction => ManagedSlackAgentAppStatusDeriver.DeriveNextAction(
        this,
        ManagedSlackAgentAppStatusDeriver.DeriveManifestState(this),
        ManagedSlackAgentAppStatusDeriver.DeriveTransportReadiness(this));

    public int DesiredManifestVersion { get; set; }
    public string DesiredManifestHash { get; set; } = string.Empty;
    public int? AppliedManifestVersion { get; set; }
    public string? AppliedManifestHash { get; set; }
    public string VerifiedScopesJson { get; set; } = "[]";
    public string InstallUrl { get; set; } = string.Empty;
    public string RuntimeCredentialValidationState { get; set; } = SlackRuntimeCredentialValidationState.NotProvided;

    public int OperationFence { get; set; }
    public string? OperationId { get; set; }
    public string? OperationKind { get; set; }
    public DateTimeOffset? OperationStartedAt { get; set; }
    public string? UnknownOutcome { get; set; }
    public string? ErrorClass { get; set; }

    public string? AuthorizationAttemptId { get; set; }
    public DateTimeOffset? AuthorizedAt { get; set; }
    public string? AuthorizationExpiresAt { get; set; }

    public string ClientSecretRef { get; set; } = string.Empty;
    public string SigningSecretRef { get; set; } = string.Empty;
    public string AppLevelTokenRef { get; set; } = string.Empty;
    public string BotTokenRef { get; set; } = string.Empty;
    public string BindingState { get; set; } = SlackAgentAppBindingState.Pending;
    public string? BindingErrorClass { get; set; }
    public string AuditJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public void TransitionAppLifecycle(string nextLifecycle)
    {
        SlackStateTransitions.RequireAgentAppLifecycleTransition(AppLifecycle, nextLifecycle);
        AppLifecycle = nextLifecycle;
    }

    public void TransitionAuthorization(string nextAuthorization)
    {
        SlackStateTransitions.RequireAuthorizationTransition(Authorization, nextAuthorization);
        Authorization = nextAuthorization;
    }

    public void TransitionBindingState(string nextBindingState)
    {
        SlackStateTransitions.RequireBindingTransition(BindingState, nextBindingState);
        BindingState = nextBindingState;
    }

    public void StageRuntimeCredentials(
        string botTokenRef,
        string appLevelTokenRef,
        string botUserId,
        string verifiedScopesJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botTokenRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(appLevelTokenRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(botUserId);
        if (AppLifecycle != SlackAppLifecycle.Created || string.IsNullOrWhiteSpace(AppId))
            throw new InvalidOperationException("Runtime credentials require a created Agent App with a known App id.");
        SlackStateTransitions.RequireRuntimeCredentialValidationTransition(
            RuntimeCredentialValidationState,
            SlackRuntimeCredentialValidationState.Candidate);
        BotTokenRef = botTokenRef.Trim();
        AppLevelTokenRef = appLevelTokenRef.Trim();
        BotUserId = botUserId.Trim();
        VerifiedScopesJson = string.IsNullOrWhiteSpace(verifiedScopesJson) ? "[]" : verifiedScopesJson;
        Authorization = SlackAuthorizationState.Authorized;
        RuntimeCredentialValidationState = SlackRuntimeCredentialValidationState.Candidate;
    }

    public void ApplyCredentialValidation(string validationState)
    {
        if (validationState is not (SlackRuntimeCredentialValidationState.Verified
            or SlackRuntimeCredentialValidationState.Failed
            or SlackRuntimeCredentialValidationState.AwaitingSocket))
            throw new ArgumentException("A credential validation result must be 'verified', 'awaiting_socket' or 'failed'.", nameof(validationState));
        SlackStateTransitions.RequireRuntimeCredentialValidationTransition(
            RuntimeCredentialValidationState,
            validationState);
        RuntimeCredentialValidationState = validationState;
    }
}

public static class SlackAppLifecycle
{
    public const string NotCreated = "not_created";
    public const string Creating = "creating";
    public const string CreateUnknown = "create_unknown";
    public const string Created = "created";
    public const string Deleting = "deleting";
    public const string DeleteUnknown = "delete_unknown";
    public const string Deleted = "deleted";
}

public static class SlackAuthorizationState
{
    public const string NotStarted = "not_started";
    public const string AwaitingUser = "awaiting_user";
    public const string PendingAdmin = "pending_admin";
    public const string Authorized = "authorized";
    public const string ExpiredOrCancelled = "expired_or_cancelled";
    public const string Revoked = "revoked";
}

public static class SlackManifestState
{
    public const string Desired = "desired";
    public const string Applied = "applied";
    public const string DriftKnown = "drift_known";
}

public static class SlackTransportReadiness
{
    public const string NotReady = "not_ready";
    public const string Ready = "ready";
}
