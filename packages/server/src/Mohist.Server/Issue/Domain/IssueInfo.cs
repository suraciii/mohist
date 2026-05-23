namespace Mohist.Server.Issue.Domain;

[GenerateSerializer]
public class IssueInfo
{
    [Id(0)] public string Id { get; set; } = null!;
    [Id(1)] public string ProjectId { get; set; } = null!;
    [Id(2)] public int Number { get; set; }
    [Id(3)] public string Title { get; set; } = null!;
    [Id(4)] public string? Body { get; set; }
    [Id(5)] public string Status { get; set; } = "Draft";
    [Id(6)] public string[] Labels { get; set; } = [];
    [Id(7)] public string Priority { get; set; } = "p2";
    [Id(8)] public string? WorkflowRunId { get; set; }
    [Id(9)] public string CreatedAt { get; set; } = "";
    [Id(10)] public string UpdatedAt { get; set; } = "";
}
