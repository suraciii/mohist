namespace Mohist.Server.Epics;

public class EpicRow
{
    public string Id { get; set; } = null!;
    public string ProjectId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string Description { get; set; } = "";
    public string Priority { get; set; } = "p2";
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
