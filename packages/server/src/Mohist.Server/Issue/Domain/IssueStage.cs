namespace Mohist.Server.Issue.Domain;

public class StageApproval
{
    public string Stage { get; set; } = null!;
    public string Status { get; set; } = null!; // pending, awaiting, approved, rejected, error
    public string? OutputJson { get; set; }
    public string RequestedAt { get; set; } = null!;
    public string? RespondedAt { get; set; }
}
