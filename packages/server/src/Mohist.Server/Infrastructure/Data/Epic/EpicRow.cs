namespace Mohist.Server.Infrastructure.Data.Epic;

public class EpicRow
{
    public string Id { get; set; } = null!;
    public string ProjectId { get; set; } = null!;
    public int Number { get; set; }
    public string Title { get; set; } = null!;
    public string Description { get; set; } = "";
    public string Priority { get; set; } = "p2";
    public string Status { get; set; } = "active";
    public string? PauseReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
