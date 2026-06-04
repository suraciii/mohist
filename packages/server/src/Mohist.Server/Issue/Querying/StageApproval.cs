namespace Mohist.Server.Issue.Querying;

public class StageApproval
{
    public string Stage { get; init; } = null!;
    public string Status { get; init; } = null!;
    public DateTime RequestedAt { get; init; }
    public DateTime? RespondedAt { get; init; }
}
