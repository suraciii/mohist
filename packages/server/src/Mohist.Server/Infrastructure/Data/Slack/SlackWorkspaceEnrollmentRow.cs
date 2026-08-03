using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Infrastructure.Data.Slack;

public sealed class SlackWorkspaceEnrollmentRow
{
    public string Id { get; set; } = string.Empty;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string Lifecycle { get; set; } = string.Empty;
    public string ManagerCapability { get; set; } = string.Empty;
    public string? CapabilityReason { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public string PlanCode { get; set; } = string.Empty;
    public int ManagedAppLimit { get; set; }
    public string ManagerCredentialRef { get; set; } = string.Empty;
    public string ManagerAppId { get; set; } = string.Empty;
    public string ManagerBotUserId { get; set; } = string.Empty;
    public string ManagerTransportKind { get; set; } = SlackManagerTransportKind.Socket;
    public string ManagerReadiness { get; set; } = SlackManagerReadiness.Unknown;
    public string ManagerActorId { get; set; } = string.Empty;
    public string? ClaimedSlackUserId { get; set; }
    public string? ManagerClaimHash { get; set; }
    public DateTimeOffset? ManagerClaimIssuedAt { get; set; }
    public DateTimeOffset? ManagerClaimExpiresAt { get; set; }
    public DateTimeOffset? ManagerClaimConsumedAt { get; set; }
    public string AuditJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
