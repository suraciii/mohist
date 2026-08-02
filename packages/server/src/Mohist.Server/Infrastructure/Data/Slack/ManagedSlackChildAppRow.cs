namespace Mohist.Server.Infrastructure.Data.Slack;

public sealed class ManagedSlackChildAppRow
{
    public string Id { get; set; } = string.Empty;
    public string EnrollmentId { get; set; } = string.Empty;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string AgentConnectionId { get; set; } = string.Empty;
    public string? PublicIngressBaseUrl { get; set; }
    public string AppId { get; set; } = string.Empty;
    public string BotUserId { get; set; } = string.Empty;

    public string AppLifecycle { get; set; } = string.Empty;
    public string Authorization { get; set; } = string.Empty;
    public string TransportKind { get; set; } = string.Empty;

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
    public string BindingState { get; set; } = string.Empty;
    public string? BindingErrorClass { get; set; }
    public string AuditJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
