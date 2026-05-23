namespace Mohist.Server.Issue.Domain;

[GenerateSerializer]
public class IssueInfo
{
    [Id(0)]  public string Id { get; set; } = null!;
    [Id(1)]  public int Number { get; set; }
    [Id(2)]  public string Title { get; set; } = null!;
    [Id(3)]  public string? Body { get; set; }
    [Id(4)]  public string Stage { get; set; } = "backlog";
    [Id(5)]  public string Status { get; set; } = "active";
    [Id(6)]  public string ProjectId { get; set; } = null!;
    [Id(7)]  public string? ProjectName { get; set; }
    [Id(8)]  public string[] Labels { get; set; } = [];
    [Id(9)]  public string Priority { get; set; } = "p2";
    [Id(10)] public string? Model { get; set; }
    [Id(11)] public Dictionary<string, string>? StageModels { get; set; }
    [Id(12)] public string CreatedAt { get; set; } = "";
    [Id(13)] public string UpdatedAt { get; set; } = "";
    [Id(14)] public string? ArchivedAt { get; set; }
    [Id(15)] public ApprovalState? ApprovalState { get; set; }
    [Id(16)] public string? MergeState { get; set; }
    [Id(17)] public int? RetryCount { get; set; }
    [Id(18)] public int? ConflictRetryCount { get; set; }
    [Id(19)] public string? BlockedReason { get; set; }
    [Id(20)] public string? WorkflowRunId { get; set; }
}
