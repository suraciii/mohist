namespace Mohist.Server.Infrastructure.Data.Issue;

public sealed class AgentInputAttachmentReservationRow
{
    public string ReservationId { get; set; } = null!;
    public string ProjectId { get; set; } = null!;
    public string AttachmentId { get; set; } = null!;
    public string OwnerId { get; set; } = null!;
    public string Status { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
