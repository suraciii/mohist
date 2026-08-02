namespace Mohist.Server.Infrastructure.Data.Issue;

public class AttachmentRow
{
    public string Id { get; set; } = null!;
    public string ProjectId { get; set; } = null!;
    public string? OwnerKind { get; set; }
    public string? OwnerId { get; set; }
    public string OriginalFileName { get; set; } = null!;
    public string? ContentType { get; set; }
    public long Size { get; set; }
    public string StoragePath { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public int? OwnerIssueNumber { get; set; }
    public string? Source { get; set; }
}
