namespace Mohist.Server.Issue.Domain;

[GenerateSerializer]
public class ApprovalState
{
    [Id(0)] public string Stage { get; set; } = null!;
    [Id(1)] public string Status { get; set; } = null!; // pending, awaiting, approved, rejected, error
    [Id(2)] public string? OutputJson { get; set; }
    [Id(3)] public string RequestedAt { get; set; } = null!;
    [Id(4)] public string? RespondedAt { get; set; }
}
