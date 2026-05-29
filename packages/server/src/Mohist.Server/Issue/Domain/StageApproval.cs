namespace Mohist.Server.Issue.Domain;

public class StageApproval
{
    public string Stage { get; init; } = null!;
    public string Status { get; init; } = null!;
    public string? OutputJson { get; init; }
    public DateTime RequestedAt { get; init; }
    public DateTime? RespondedAt { get; init; }
}