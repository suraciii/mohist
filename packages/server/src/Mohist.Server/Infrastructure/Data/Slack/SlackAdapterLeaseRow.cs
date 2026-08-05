namespace Mohist.Server.Infrastructure.Data.Slack;

public sealed class SlackAdapterLeaseRow
{
    public string TargetKey { get; set; } = string.Empty;
    public int Generation { get; set; }
    public string? LeaseId { get; set; }
    public string? LeaseKind { get; set; }
    public string? AdapterId { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
