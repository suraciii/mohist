using Mohist.Server.Project.Queries;

namespace Mohist.Server.Issue.Domain;

public class Issue
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public required int Number { get; set; }
    public string Title { get; set; } = null!;
    public string? Body { get; set; }
    public string[] Labels { get; set; } = [];
    public string Priority { get; set; } = "p2";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public string? WorkflowRunId { get; set; }
    public IssueStage Stage { get; set; } = IssueStage.Backlog;
    public IssueAttention? Attention { get; set; }
    public StageApproval? StageApproval { get; set; }
    public int RetryCount { get; set; }
    public int ConflictRetryCount { get; set; }
    public string? BlockedReason { get; set; }
    public int[] PrerequisiteNumbers { get; set; } = [];
    public RepositoryInfo? Repository { get; set; }
}
