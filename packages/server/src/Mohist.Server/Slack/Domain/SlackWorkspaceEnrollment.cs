namespace Mohist.Server.Slack.Domain;

public sealed class SlackWorkspaceEnrollment
{
    public string Id { get; set; } = string.Empty;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string ManagerExternalId { get; set; } = string.Empty;
    public string Lifecycle { get; set; } = SlackEnrollmentLifecycle.Active;
    public string ManagerCapability { get; set; } = SlackManagerCapability.Unknown;
    public string? CapabilityReason { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public string PlanCode { get; set; } = string.Empty;
    public int ManagedAppLimit { get; set; }
    public string ManagerCredentialRef { get; set; } = string.Empty;
    public string AuditJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public static class SlackEnrollmentLifecycle
{
    public const string Active = "active";
    public const string Disabled = "disabled";
    public const string Removed = "removed";
}

public static class SlackManagerCapability
{
    public const string Unknown = "unknown";
    public const string Available = "available";
    public const string Unauthorized = "unauthorized";
    public const string CapacityLimited = "capacity_limited";
}
