namespace Mohist.Server.Slack.Domain;

public sealed class ManagedSlackChildApp
{
    public string Id { get; set; } = string.Empty;
    public string EnrollmentId { get; set; } = string.Empty;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string AgentConnectionId { get; set; } = string.Empty;
    public string? PublicIngressBaseUrl { get; set; }
    public string AppId { get; set; } = string.Empty;
    public string BotUserId { get; set; } = string.Empty;

    public string AppLifecycle { get; set; } = SlackAppLifecycle.NotCreated;
    public string Authorization { get; set; } = SlackAuthorizationState.NotStarted;
    public string ManifestState => ManagedSlackChildAppStatusDeriver.DeriveManifestState(this);
    public string TransportKind { get; set; } = SlackTransportKind.Socket;
    public string TransportReadiness => ManagedSlackChildAppStatusDeriver.DeriveTransportReadiness(this);
    public string NextAction => ManagedSlackChildAppStatusDeriver.DeriveNextAction(
        this,
        ManagedSlackChildAppStatusDeriver.DeriveManifestState(this),
        ManagedSlackChildAppStatusDeriver.DeriveTransportReadiness(this));

    public int DesiredManifestVersion { get; set; }
    public string DesiredManifestHash { get; set; } = string.Empty;
    public int? AppliedManifestVersion { get; set; }
    public string? AppliedManifestHash { get; set; }
    public string VerifiedScopesJson { get; set; } = "[]";

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
    public string BindingState { get; set; } = SlackChildAppBindingState.Pending;
    public string? BindingErrorClass { get; set; }
    public string AuditJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public void TransitionAppLifecycle(string nextLifecycle)
    {
        SlackStateTransitions.RequireChildAppLifecycleTransition(AppLifecycle, nextLifecycle);
        AppLifecycle = nextLifecycle;
    }

    public void TransitionAuthorization(string nextAuthorization)
    {
        SlackStateTransitions.RequireAuthorizationTransition(Authorization, nextAuthorization);
        Authorization = nextAuthorization;
    }

    public void SetTransportKind(string transportKind)
    {
        SlackStateTransitions.RequireTransportKind(transportKind);
        TransportKind = transportKind;
    }

    public void TransitionBindingState(string nextBindingState)
    {
        SlackStateTransitions.RequireBindingTransition(BindingState, nextBindingState);
        BindingState = nextBindingState;
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

public static class SlackTransportKind
{
    public const string Socket = "socket";
    public const string Https = "https";
}

public static class SlackTransportReadiness
{
    public const string NotReady = "not_ready";
    public const string Ready = "ready";
}
