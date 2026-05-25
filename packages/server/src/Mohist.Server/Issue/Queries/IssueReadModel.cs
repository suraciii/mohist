using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.WorkflowProfiles;

namespace Mohist.Server.Issue.Queries;

public class IssueReadModel
{
    public string Id { get; set; } = null!;
    public int Number { get; set; }
    public string Title { get; set; } = null!;
    public string? Body { get; set; }
    public string Stage { get; set; } = "backlog";
    public string Status { get; set; } = "active";
    public string ProjectId { get; set; } = null!;
    public string? ProjectName { get; set; }
    public string[] Labels { get; set; } = [];
    public string Priority { get; set; } = "p2";
    public string? Model { get; set; }
    public Dictionary<string, string>? StageModels { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string? ArchivedAt { get; set; }
    public ApprovalState? ApprovalState { get; set; }
    public string? MergeState { get; set; }
    public int? RetryCount { get; set; }
    public int? ConflictRetryCount { get; set; }
    public string? BlockedReason { get; set; }
    public IssueAttention? Attention { get; set; }
    public string? WorkflowRunId { get; set; }
    public string WorkflowProfileId { get; set; } = IssueWorkflowProfiles.DefaultId;
    public int[] PrerequisiteNumbers { get; set; } = [];
    public IssueCommentDto[] Comments { get; set; } = [];
    public IssuePrerequisiteSummary[] Prerequisites { get; set; } = [];
    public IssueStartEligibility StartEligibility { get; set; } = IssueStartEligibility.Ready();
    public IssuePrimaryEpic? PrimaryEpic { get; set; }
}
